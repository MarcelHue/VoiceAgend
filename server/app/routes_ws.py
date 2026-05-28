import json
import time

from fastapi import APIRouter, WebSocket, WebSocketDisconnect
from sqlalchemy import select

from .config import settings
from .db import SessionLocal, Transcription, Profile
from .security import authenticate_api_key, resolve_api_key
from .transcribe import service, TranscriptionError

router = APIRouter()


@router.websocket("/ws/transcribe")
async def ws_transcribe(ws: WebSocket):
    await ws.accept()
    try:
        # 1) JSON-Header mit Auth + Metadaten
        header_raw = await ws.receive_text()
        header = json.loads(header_raw)
        api_key = header.get("api_key", "")
        language = header.get("language")

        async with SessionLocal() as db:
            ak, user = await resolve_api_key(api_key, db)
            profile = None
            if ak is not None:
                pres = await db.execute(select(Profile).where(Profile.api_key_id == ak.id))
                profile = pres.scalar_one_or_none()
        if not user:
            await ws.send_json({"error": "auth_failed"})
            await ws.close(code=4401)
            return

        # 2) Binary-Chunks bis end-Marker
        max_bytes = settings.max_upload_mb * 1024 * 1024
        buf = bytearray()
        while True:
            msg = await ws.receive()
            if msg.get("type") == "websocket.disconnect":
                return
            if "bytes" in msg and msg["bytes"] is not None:
                chunk = msg["bytes"]
                if len(buf) + len(chunk) > max_bytes:
                    await ws.send_json({"error": "too_large"})
                    await ws.close(code=4413)
                    return
                buf.extend(chunk)
            elif "text" in msg and msg["text"] is not None:
                try:
                    ctrl = json.loads(msg["text"])
                except json.JSONDecodeError:
                    continue
                if ctrl.get("end"):
                    break

        await ws.send_json({"status": "processing"})

        # Sprach-abhängige Prompt-Auswahl:
        # - explizit "de" → prompt_de (Fallback: legacy prompt)
        # - explizit "en" → prompt_en (Fallback: legacy prompt)
        # - Auto-Modus (keine Sprache) → kein Prompt, damit Whisper neutral
        #   detektieren kann. Ein deutscher Prompt zwingt Whisper sonst auf Deutsch,
        #   selbst wenn der User Englisch spricht.
        chosen_prompt: str | None = None
        if profile is not None and language:
            lang_lc = language.lower()
            if lang_lc == "de":
                chosen_prompt = profile.prompt_de or profile.prompt
            elif lang_lc == "en":
                chosen_prompt = profile.prompt_en or profile.prompt
            else:
                chosen_prompt = profile.prompt

        t0 = time.perf_counter()
        try:
            text, detected_lang = await service.transcribe(
                bytes(buf),
                language=language,
                model=profile.model if profile else None,
                prompt=chosen_prompt,
                temperature=profile.temperature if profile else None,
            )
        except TranscriptionError as e:
            await ws.send_json({"error": "whisper_failed", "detail": str(e)})
            await ws.close(code=1011)
            return
        proc_ms = int((time.perf_counter() - t0) * 1000)

        async with SessionLocal() as db:
            row = Transcription(
                user_id=user.id,
                duration_ms=0,  # Backend liefert keine duration mehr → 0; können wir später füllen
                audio_bytes=len(buf),
                language=detected_lang,
                text=text,
                processing_ms=proc_ms,
            )
            db.add(row)
            await db.commit()

        await ws.send_json({
            "text": text,
            "language": detected_lang,
            "processing_ms": proc_ms,
        })
        await ws.close()

    except WebSocketDisconnect:
        return
    except Exception as e:
        try:
            await ws.send_json({"error": "server_error", "detail": str(e)})
            await ws.close(code=1011)
        except Exception:
            pass
