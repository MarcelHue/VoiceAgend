from typing import Annotated
from datetime import datetime
from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from .db import get_session, User, ApiKey
from .security import (
    require_admin, get_current_user, hash_password, generate_api_key,
)

router = APIRouter(prefix="/api", tags=["users"])


class UserOut(BaseModel):
    id: int
    username: str
    is_admin: bool
    is_active: bool
    created_at: datetime

    class Config:
        from_attributes = True


class UserCreate(BaseModel):
    username: str = Field(min_length=3, max_length=64)
    password: str = Field(min_length=8)
    is_admin: bool = False


class UserUpdate(BaseModel):
    password: str | None = Field(default=None, min_length=8)
    is_admin: bool | None = None
    is_active: bool | None = None


class ApiKeyOut(BaseModel):
    id: int
    name: str
    prefix: str
    created_at: datetime
    last_used_at: datetime | None

    class Config:
        from_attributes = True


class ApiKeyCreate(BaseModel):
    name: str = Field(min_length=1, max_length=64)


class ApiKeyCreateResponse(ApiKeyOut):
    key: str  # full key, shown only at creation time


@router.get("/users", response_model=list[UserOut])
async def list_users(
    _: Annotated[User, Depends(require_admin)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    res = await db.execute(select(User).order_by(User.id))
    return list(res.scalars())


@router.post("/users", response_model=UserOut, status_code=201)
async def create_user(
    payload: UserCreate,
    _: Annotated[User, Depends(require_admin)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    exists = await db.execute(select(User).where(User.username == payload.username))
    if exists.scalar_one_or_none():
        raise HTTPException(status_code=409, detail="Username taken")
    user = User(
        username=payload.username,
        password_hash=hash_password(payload.password),
        is_admin=payload.is_admin,
    )
    db.add(user)
    await db.commit()
    await db.refresh(user)
    return user


@router.patch("/users/{user_id}", response_model=UserOut)
async def update_user(
    user_id: int,
    payload: UserUpdate,
    _: Annotated[User, Depends(require_admin)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    res = await db.execute(select(User).where(User.id == user_id))
    user = res.scalar_one_or_none()
    if not user:
        raise HTTPException(status_code=404)
    if payload.password is not None:
        user.password_hash = hash_password(payload.password)
    if payload.is_admin is not None:
        user.is_admin = payload.is_admin
    if payload.is_active is not None:
        user.is_active = payload.is_active
    await db.commit()
    await db.refresh(user)
    return user


@router.delete("/users/{user_id}", status_code=204)
async def delete_user(
    user_id: int,
    admin: Annotated[User, Depends(require_admin)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    if user_id == admin.id:
        raise HTTPException(status_code=400, detail="Cannot delete self")
    res = await db.execute(select(User).where(User.id == user_id))
    user = res.scalar_one_or_none()
    if not user:
        raise HTTPException(status_code=404)
    await db.delete(user)
    await db.commit()


@router.get("/api-keys", response_model=list[ApiKeyOut])
async def list_my_keys(
    user: Annotated[User, Depends(get_current_user)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    res = await db.execute(select(ApiKey).where(ApiKey.user_id == user.id).order_by(ApiKey.id))
    return list(res.scalars())


@router.post("/api-keys", response_model=ApiKeyCreateResponse, status_code=201)
async def create_my_key(
    payload: ApiKeyCreate,
    user: Annotated[User, Depends(get_current_user)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    full, prefix, h = generate_api_key()
    key = ApiKey(user_id=user.id, name=payload.name, prefix=prefix, key_hash=h)
    db.add(key)
    await db.commit()
    await db.refresh(key)
    return ApiKeyCreateResponse(
        id=key.id, name=key.name, prefix=key.prefix,
        created_at=key.created_at, last_used_at=key.last_used_at, key=full,
    )


@router.post("/api-keys/{key_id}/rotate", response_model=ApiKeyCreateResponse)
async def rotate_my_key(
    key_id: int,
    user: Annotated[User, Depends(get_current_user)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    """Vergibt den Schlüsselwert neu, OHNE den Datensatz (und damit das daran
    hängende Profil/die Einstellungen) zu löschen. Der alte Wert wird sofort
    ungültig; der neue wird einmalig zurückgegeben."""
    res = await db.execute(select(ApiKey).where(ApiKey.id == key_id, ApiKey.user_id == user.id))
    key = res.scalar_one_or_none()
    if not key:
        raise HTTPException(status_code=404)
    full, prefix, h = generate_api_key()
    key.prefix = prefix
    key.key_hash = h
    key.last_used_at = None
    await db.commit()
    await db.refresh(key)
    return ApiKeyCreateResponse(
        id=key.id, name=key.name, prefix=key.prefix,
        created_at=key.created_at, last_used_at=key.last_used_at, key=full,
    )


@router.delete("/api-keys/{key_id}", status_code=204)
async def delete_my_key(
    key_id: int,
    user: Annotated[User, Depends(get_current_user)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    res = await db.execute(select(ApiKey).where(ApiKey.id == key_id, ApiKey.user_id == user.id))
    key = res.scalar_one_or_none()
    if not key:
        raise HTTPException(status_code=404)
    await db.delete(key)
    await db.commit()
