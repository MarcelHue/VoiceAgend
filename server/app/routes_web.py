"""JWT-authentifizierte Endpoints für die Web-/PWA-Oberfläche.

Die Web-UI loggt sich per Benutzer-JWT ein und besitzt NICHT den rohen API-Key
(der existiert nur einmalig bei Erstellung). Daher adressiert sie Profil und
Transkription über die Key-ID; ein Ownership-Check stellt sicher, dass der Key
dem eingeloggten User gehört. Die Logik (Profil-Serialisierung, Prompt-Auswahl)
wird mit den bestehenden API-Key-Routen geteilt.
"""
import json
import time
from datetime import datetime
from typing import Annotated

from fastapi import APIRouter, Depends, File, Form, HTTPException, UploadFile
from pydantic import BaseModel, Field
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from .config import settings
from .db import ApiKey, Profile, Transcription, User, get_session
from .security import get_current_user
from .transcribe import service, TranscriptionError, select_prompt
from .routes_profile import ProfileOut, _ensure_profile, _profile_for

router = APIRouter(prefix="/api/web", tags=["web"])


async def _owned_key(key_id: int, user: User, db: AsyncSession) -> ApiKey:
    res = await db.execute(select(ApiKey).where(ApiKey.id == key_id))
    ak = res.scalar_one_or_none()
    if ak is None:
        raise HTTPException(status_code=404, detail="API key not found")
    if ak.user_id != user.id:
        raise HTTPException(status_code=403, detail="Not your API key")
    return ak


class LanguageUpdate(BaseModel):
    language: str = Field(default="", max_length=16)


class WebProfileUpdate(BaseModel):
    model: str | None = Field(default=None, max_length=255)
    prompt_de: str | None = Field(default=None, max_length=4000)
    prompt_en: str | None = Field(default=None, max_length=4000)
    language_prompts: dict[str, str] | None = None
    temperature: float | None = Field(default=None, ge=0.0, le=1.0)


@router.get("/keys/{key_id}/profile", response_model=ProfileOut)
async def web_get_profile(
    key_id: int,
    user: Annotated[User, Depends(get_current_user)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    ak = await _owned_key(key_id, user, db)
    return await _profile_for(ak, db)


@router.put("/keys/{key_id}/language", response_model=ProfileOut)
async def web_set_language(
    key_id: int,
    payload: LanguageUpdate,
    user: Annotated[User, Depends(get_current_user)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    """Setzt die Erkennungssprache im Profil. MERGED in client_settings, damit
    Desktop-Präferenzen (theme, enabledLanguages, Hotkey, …) erhalten bleiben."""
    ak = await _owned_key(key_id, user, db)
    profile = await _ensure_profile(ak, db)
    cs: dict = {}
    if profile.client_settings:
        try:
            cs = json.loads(profile.client_settings)
        except (ValueError, TypeError):
            cs = {}
    cs["language"] = payload.language or ""
    profile.client_settings = json.dumps(cs, ensure_ascii=False)
    # Neuer Timestamp → beim nächsten Sync zieht der Desktop die (gemergte) Version.
    profile.client_settings_updated_at = datetime.utcnow()
    await db.commit()
    return await _profile_for(ak, db)


@router.put("/keys/{key_id}/profile", response_model=ProfileOut)
async def web_update_profile(
    key_id: int,
    payload: WebProfileUpdate,
    user: Annotated[User, Depends(get_current_user)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    """Setzt Profil-Spaltenfelder (Modell/Prompts/Temperatur). Fasst client_settings
    NICHT an — die Sprache läuft über den /language-Endpoint."""
    ak = await _owned_key(key_id, user, db)
    profile = await _ensure_profile(ak, db)
    if payload.model is not None:
        profile.model = payload.model or None
    if payload.prompt_de is not None:
        profile.prompt_de = payload.prompt_de or None
    if payload.prompt_en is not None:
        profile.prompt_en = payload.prompt_en or None
    if payload.language_prompts is not None:
        cleaned = {
            k.lower(): v
            for k, v in payload.language_prompts.items()
            if v and v.strip() and k.lower() not in ("de", "en")
        }
        profile.language_prompts = json.dumps(cleaned, ensure_ascii=False) if cleaned else None
    if payload.temperature is not None:
        profile.temperature = payload.temperature
    await db.commit()
    return await _profile_for(ak, db)


@router.post("/keys/{key_id}/transcribe")
async def web_transcribe(
    key_id: int,
    user: Annotated[User, Depends(get_current_user)],
    db: Annotated[AsyncSession, Depends(get_session)],
    file: Annotated[UploadFile, File()],
    language: Annotated[str | None, Form()] = None,
):
    """Nimmt eine im Browser aufgenommene Audiodatei (webm/opus) entgegen und gibt
    den transkribierten Text zurück. Profil (Modell/Prompt/Temperatur) wird über
    die Key-ID aufgelöst."""
    ak = await _owned_key(key_id, user, db)
    profile = await _ensure_profile(ak, db)

    audio = await file.read()
    if len(audio) < 1024:
        raise HTTPException(status_code=400, detail="audio too short")
    if len(audio) > settings.max_upload_mb * 1024 * 1024:
        raise HTTPException(status_code=413, detail="audio too large")

    # language: explizit aus dem Formular ("" = Auto), sonst die im Profil
    # gespeicherte Sprache.
    if language is None:
        cs: dict = {}
        if profile.client_settings:
            try:
                cs = json.loads(profile.client_settings)
            except (ValueError, TypeError):
                cs = {}
        lang = cs.get("language") or ""
    else:
        lang = language
    lang = lang or None  # "" → None = Auto

    prompt = select_prompt(profile, lang)
    t0 = time.perf_counter()
    try:
        text, detected_lang = await service.transcribe(
            audio,
            language=lang,
            model=profile.model,
            prompt=prompt,
            temperature=profile.temperature,
            filename=file.filename or "audio.webm",
            content_type=file.content_type or "audio/webm",
        )
    except TranscriptionError as e:
        raise HTTPException(status_code=502, detail=f"whisper failed: {e}")

    proc_ms = int((time.perf_counter() - t0) * 1000)
    db.add(
        Transcription(
            user_id=user.id,
            duration_ms=0,
            audio_bytes=len(audio),
            language=detected_lang,
            text=text,
            processing_ms=proc_ms,
        )
    )
    await db.commit()
    return {"text": text, "language": detected_lang, "processing_ms": proc_ms}
