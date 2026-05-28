import json
from datetime import datetime
from typing import Annotated, Any
from urllib.parse import quote

import httpx
from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from .config import settings
from .db import ApiKey, Profile, User, get_session
from .security import require_api_key

router = APIRouter(prefix="/api/v1", tags=["profile"])


class ModelOut(BaseModel):
    id: str
    object: str | None = None
    owned_by: str | None = None


class ProfileOut(BaseModel):
    api_key_id: int
    api_key_name: str
    model: str | None
    prompt: str | None
    prompt_de: str | None = None
    prompt_en: str | None = None
    temperature: float
    # Gateway-Fallback (env WHISPER_MODEL) — wird verwendet, wenn profile.model = NULL.
    # Der Client braucht das, um zu prüfen, ob der "Server-Default" überhaupt installiert ist.
    server_default_model: str | None = None
    # Software-Präferenzen (Theme, Hotkey, …). Wir geben das als dict zurück, damit der
    # Client neue Keys hinzufügen kann, ohne das Server-Schema mit aufzubohren.
    client_settings: dict[str, Any] | None = None
    client_settings_updated_at: datetime | None = None


class ProfileUpdate(BaseModel):
    model: str | None = Field(default=None, max_length=255)
    prompt: str | None = Field(default=None, max_length=4000)
    prompt_de: str | None = Field(default=None, max_length=4000)
    prompt_en: str | None = Field(default=None, max_length=4000)
    temperature: float | None = Field(default=None, ge=0.0, le=1.0)
    client_settings: dict[str, Any] | None = None
    # ISO-8601-Timestamp wann der Client diese Settings zuletzt geändert hat (UTC).
    # Server akzeptiert nur, wenn der Wert neuer ist als das Stored — schützt vor
    # Race Conditions zwischen mehreren Geräten.
    client_settings_updated_at: datetime | None = None


@router.get("/models", response_model=list[ModelOut])
async def list_models(_: Annotated[tuple[ApiKey, User], Depends(require_api_key)]):
    """Whisper-Modelle vom Speaches-Backend (gecacht/lokal verfügbar)."""
    headers = _whisper_headers()
    try:
        async with httpx.AsyncClient(timeout=10.0, headers=headers) as c:
            r = await c.get(f"{settings.whisper_url.rstrip('/')}/v1/models")
            r.raise_for_status()
            data = r.json().get("data", [])
            return [ModelOut(**m) for m in data]
    except httpx.HTTPError as e:
        raise HTTPException(status_code=502, detail=f"whisper backend unreachable: {e}")


@router.post("/models/{model_id:path}", status_code=202)
async def install_model(
    model_id: str,
    _: Annotated[tuple[ApiKey, User], Depends(require_api_key)],
):
    """Triggert den Download eines Modells im Speaches-Backend.
    Modelle können mehrere GB groß sein — Timeout ist großzügig (10 min).

    Speaches' POST-Route deklariert {model_id} NICHT als :path-Parameter,
    daher müssen wir den Slash url-encoden — sonst routet FastAPI auf der
    Speaches-Seite ins Leere (404)."""
    encoded = quote(model_id, safe="")
    headers = _whisper_headers()
    base = settings.whisper_url.rstrip("/")
    try:
        async with httpx.AsyncClient(timeout=600.0, headers=headers) as c:
            r = await c.post(f"{base}/v1/models/{encoded}")
            if r.status_code >= 400:
                raise HTTPException(
                    status_code=502,
                    detail=f"speaches HTTP {r.status_code}: {r.text[:300]}",
                )
            return {"status": "installed", "model": model_id}
    except httpx.HTTPError as e:
        raise HTTPException(status_code=502, detail=f"whisper backend unreachable: {e}")


@router.delete("/models/{model_id:path}", status_code=200)
async def uninstall_model(
    model_id: str,
    _: Annotated[tuple[ApiKey, User], Depends(require_api_key)],
):
    """Löscht ein installiertes Modell aus dem Speaches-Cache."""
    encoded = quote(model_id, safe="")
    headers = _whisper_headers()
    base = settings.whisper_url.rstrip("/")
    try:
        async with httpx.AsyncClient(timeout=60.0, headers=headers) as c:
            r = await c.delete(f"{base}/v1/models/{encoded}")
            if r.status_code >= 400:
                raise HTTPException(
                    status_code=502,
                    detail=f"speaches HTTP {r.status_code}: {r.text[:300]}",
                )
            return {"status": "uninstalled", "model": model_id}
    except httpx.HTTPError as e:
        raise HTTPException(status_code=502, detail=f"whisper backend unreachable: {e}")


def _whisper_headers() -> dict:
    return {"Authorization": f"Bearer {settings.whisper_api_key}"} if settings.whisper_api_key else {}


@router.get("/profile", response_model=ProfileOut)
async def get_profile(
    auth: Annotated[tuple[ApiKey, User], Depends(require_api_key)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    api_key, _ = auth
    return await _profile_for(api_key, db)


@router.put("/profile", response_model=ProfileOut)
async def update_profile(
    payload: ProfileUpdate,
    auth: Annotated[tuple[ApiKey, User], Depends(require_api_key)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    api_key, _ = auth
    profile = await _ensure_profile(api_key, db)
    if payload.model is not None:
        profile.model = payload.model or None
    if payload.prompt is not None:
        profile.prompt = payload.prompt or None
    if payload.prompt_de is not None:
        profile.prompt_de = payload.prompt_de or None
    if payload.prompt_en is not None:
        profile.prompt_en = payload.prompt_en or None
    if payload.temperature is not None:
        profile.temperature = payload.temperature
    if payload.client_settings is not None:
        incoming_ts = payload.client_settings_updated_at or datetime.utcnow()
        # Last-Write-Wins per Timestamp: nur überschreiben, wenn der eingehende Stand
        # neuer ist. So ist es egal, in welcher Reihenfolge mehrere Geräte syncen.
        if (
            profile.client_settings_updated_at is None
            or incoming_ts >= profile.client_settings_updated_at
        ):
            profile.client_settings = json.dumps(payload.client_settings, ensure_ascii=False)
            profile.client_settings_updated_at = incoming_ts
    await db.commit()
    await db.refresh(profile)
    return await _profile_for(api_key, db)


async def _ensure_profile(api_key: ApiKey, db: AsyncSession) -> Profile:
    res = await db.execute(select(Profile).where(Profile.api_key_id == api_key.id))
    p = res.scalar_one_or_none()
    if p is None:
        p = Profile(api_key_id=api_key.id, model=None, prompt=None, temperature=0.0)
        db.add(p)
        await db.commit()
        await db.refresh(p)
    return p


async def _profile_for(api_key: ApiKey, db: AsyncSession) -> ProfileOut:
    p = await _ensure_profile(api_key, db)
    client_settings: dict[str, Any] | None = None
    if p.client_settings:
        try:
            client_settings = json.loads(p.client_settings)
        except (ValueError, TypeError):
            client_settings = None
    return ProfileOut(
        api_key_id=api_key.id,
        api_key_name=api_key.name,
        model=p.model,
        prompt=p.prompt,
        prompt_de=p.prompt_de,
        prompt_en=p.prompt_en,
        temperature=p.temperature,
        server_default_model=settings.whisper_model or None,
        client_settings=client_settings,
        client_settings_updated_at=p.client_settings_updated_at,
    )
