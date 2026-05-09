import secrets
import hashlib
from datetime import datetime, timedelta, timezone
from typing import Annotated

from fastapi import Depends, HTTPException, status, Request
from fastapi.security import OAuth2PasswordBearer
from jose import jwt, JWTError
from passlib.context import CryptContext
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from .config import settings
from .db import get_session, User, ApiKey  # noqa: F401  (Request used by require_api_key)

pwd = CryptContext(schemes=["bcrypt"], deprecated="auto")
oauth2_scheme = OAuth2PasswordBearer(tokenUrl="/api/auth/login", auto_error=False)


def hash_password(plain: str) -> str:
    return pwd.hash(plain)


def verify_password(plain: str, hashed: str) -> bool:
    return pwd.verify(plain, hashed)


def create_access_token(sub: str) -> str:
    exp = datetime.now(timezone.utc) + timedelta(minutes=settings.jwt_ttl_minutes)
    return jwt.encode({"sub": sub, "exp": exp}, settings.jwt_secret, algorithm=settings.jwt_alg)


def generate_api_key() -> tuple[str, str, str]:
    """Returns (full_key, prefix, sha256_hash). Full key shown to user once."""
    raw = secrets.token_urlsafe(32)
    full = f"va_{raw}"
    prefix = full[:10]
    h = hashlib.sha256(full.encode()).hexdigest()
    return full, prefix, h


def hash_api_key(full: str) -> str:
    return hashlib.sha256(full.encode()).hexdigest()


async def get_current_user(
    token: Annotated[str | None, Depends(oauth2_scheme)],
    db: Annotated[AsyncSession, Depends(get_session)],
) -> User:
    if not token:
        raise HTTPException(status_code=401, detail="Not authenticated")
    try:
        payload = jwt.decode(token, settings.jwt_secret, algorithms=[settings.jwt_alg])
        username = payload.get("sub")
    except JWTError:
        raise HTTPException(status_code=401, detail="Invalid token")
    res = await db.execute(select(User).where(User.username == username))
    user = res.scalar_one_or_none()
    if not user or not user.is_active:
        raise HTTPException(status_code=401, detail="User inactive")
    return user


async def require_admin(user: Annotated[User, Depends(get_current_user)]) -> User:
    if not user.is_admin:
        raise HTTPException(status_code=403, detail="Admin required")
    return user


async def authenticate_api_key(key: str, db: AsyncSession) -> User | None:
    api_key, user = await resolve_api_key(key, db)
    return user


async def resolve_api_key(key: str, db: AsyncSession) -> tuple["ApiKey | None", User | None]:
    if not key or not key.startswith("va_"):
        return None, None
    h = hash_api_key(key)
    res = await db.execute(select(ApiKey).where(ApiKey.key_hash == h))
    api_key = res.scalar_one_or_none()
    if not api_key:
        return None, None
    api_key.last_used_at = datetime.utcnow()
    res2 = await db.execute(select(User).where(User.id == api_key.user_id))
    user = res2.scalar_one_or_none()
    await db.commit()
    if not user or not user.is_active:
        return api_key, None
    return api_key, user


async def require_api_key(
    request: "Request",
    db: Annotated[AsyncSession, Depends(get_session)],
) -> tuple["ApiKey", User]:
    """Header-basierte API-Key-Auth: 'X-API-Key: va_...' oder 'Authorization: Bearer va_...'."""
    key = request.headers.get("X-API-Key")
    if not key:
        auth = request.headers.get("Authorization", "")
        if auth.startswith("Bearer va_"):
            key = auth.removeprefix("Bearer ").strip()
    if not key:
        raise HTTPException(status_code=401, detail="API key required (X-API-Key header)")
    api_key, user = await resolve_api_key(key, db)
    if not api_key or not user:
        raise HTTPException(status_code=401, detail="Invalid API key")
    return api_key, user
