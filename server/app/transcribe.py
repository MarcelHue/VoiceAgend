import json
import re
from difflib import SequenceMatcher

import httpx
from .config import settings


def select_prompt(profile, language: str | None) -> str | None:
    """Wählt den Initial-Prompt passend zur Erkennungssprache.

    Reihenfolge: language_prompts[lang] → de/en-Spalten → Legacy-`prompt`.
    Auto-Modus (kein language) → None, damit Whisper neutral detektiert.
    Geteilt von WebSocket-Transcribe (Desktop) und Web-Transcribe (PWA).
    """
    if profile is None or not language:
        return None
    lang_lc = language.lower()
    lang_map: dict = {}
    if getattr(profile, "language_prompts", None):
        try:
            lang_map = json.loads(profile.language_prompts)
        except (ValueError, TypeError):
            lang_map = {}
    mapped = lang_map.get(lang_lc)
    if mapped and mapped.strip():
        return mapped
    if lang_lc == "de":
        return profile.prompt_de or profile.prompt
    if lang_lc == "en":
        return profile.prompt_en or profile.prompt
    return profile.prompt


def _normalize(s: str) -> str:
    return re.sub(r"\s+", " ", s.strip().lower())


def _similar(a: str, b: str, threshold: float = 0.92) -> bool:
    """Whether two sentences are (near-)identical."""
    a_n, b_n = _normalize(a), _normalize(b)
    if a_n == b_n:
        return True
    # Quick length gate to avoid expensive ratio() on obvious non-matches
    short, long_ = sorted((len(a_n), len(b_n)))
    if short == 0 or short / long_ < 0.7:
        return False
    return SequenceMatcher(None, a_n, b_n).ratio() >= threshold


def _dedupe_repetitions(text: str, min_repeat: int = 3) -> str:
    """Collapse runs of identical consecutive sentences (Whisper repetition hallucinations)."""
    if not text or not text.strip():
        return text
    parts = re.split(r"(?<=[.!?…])\s+", text.strip())
    if len(parts) < min_repeat:
        return text

    out: list[str] = []
    i = 0
    while i < len(parts):
        current = parts[i]
        j = i + 1
        while j < len(parts) and _similar(parts[j], current):
            j += 1
        run = j - i
        if run >= min_repeat:
            out.append(current)
            i = j
        else:
            out.extend(parts[i:j])
            i = j
    return " ".join(out)


# Bekannte Phrasen, die Whisper bei Stille/Musik halluziniert.
# In Lowercase, werden case-insensitive am Ende des Texts geprüft.
_HALLUCINATION_TRAILS = (
    "vielen dank fürs zuschauen",
    "vielen dank für's zuschauen",
    "danke fürs zuschauen",
    "danke für's zuschauen",
    "tschüss",
    "bis zum nächsten mal",
    "untertitelung des zdf",
    "untertitel im auftrag",
    "untertitel der amara.org-community",
    "thanks for watching",
    "thank you for watching",
    "see you next time",
    "see you in the next video",
    "please subscribe",
    "subtitles by the amara.org community",
)


def _strip_hallucination_trails(text: str) -> str:
    """Schneidet bekannte Halluzinations-Phrasen am Ende des Texts ab."""
    if not text:
        return text
    stripped = text.rstrip()
    lower = stripped.lower()
    # Wiederhole, solange am Ende eine bekannte Phrase steht (entfernt Mehrfach-Ketten)
    changed = True
    while changed:
        changed = False
        for phrase in _HALLUCINATION_TRAILS:
            # Phrase kann mit . / ! / ? enden oder ohne — wir erlauben optional Satzendezeichen
            for suffix in ("", ".", "!", "?", "…", ". ", "! ", "? "):
                target = phrase + suffix
                if lower.endswith(target):
                    stripped = stripped[: -len(target)].rstrip(" .,;:!?…\n\r")
                    lower = stripped.lower()
                    changed = True
                    break
            if changed:
                break
    return stripped


class TranscriptionError(Exception):
    pass


class TranscriptionService:
    """Forwards audio to a Speaches/OpenAI-compatible Whisper backend."""

    def __init__(self) -> None:
        self._client: httpx.AsyncClient | None = None

    async def start(self) -> None:
        headers = {}
        if settings.whisper_api_key:
            headers["Authorization"] = f"Bearer {settings.whisper_api_key}"
        self._client = httpx.AsyncClient(
            base_url=settings.whisper_url.rstrip("/"),
            timeout=httpx.Timeout(settings.whisper_timeout_s),
            headers=headers,
        )

    async def stop(self) -> None:
        if self._client is not None:
            await self._client.aclose()
            self._client = None

    async def health(self) -> dict:
        assert self._client is not None
        try:
            r = await self._client.get("/health", timeout=5.0)
            return {"reachable": r.is_success, "status_code": r.status_code}
        except httpx.HTTPError as e:
            return {"reachable": False, "error": str(e)}

    async def transcribe(
        self,
        audio_bytes: bytes,
        language: str | None = None,
        model: str | None = None,
        prompt: str | None = None,
        temperature: float | None = None,
        filename: str = "audio.ogg",
        content_type: str = "audio/ogg",
    ) -> tuple[str, str | None]:
        assert self._client is not None
        # filename/content_type sind nur Hints — Speaches/ffmpeg erkennt das Format
        # inhaltsbasiert. Der Browser liefert z.B. audio/webm;codecs=opus.
        files = {"file": (filename, audio_bytes, content_type)}
        data: dict[str, str] = {
            "model": model or settings.whisper_model,
            "response_format": "json",
            # Speaches-Extension: schneidet stille Passagen vorab raus,
            # eine der häufigsten Quellen für Whisper-Halluzinationen.
            "vad_filter": "true",
        }
        lang = language or settings.default_language
        if lang:
            data["language"] = lang
        if prompt:
            data["prompt"] = prompt
        if temperature is not None:
            data["temperature"] = str(temperature)
        try:
            r = await self._client.post("/v1/audio/transcriptions", files=files, data=data)
        except httpx.HTTPError as e:
            raise TranscriptionError(f"connection to whisper backend failed: {e}") from e
        if r.status_code >= 400:
            raise TranscriptionError(f"whisper backend HTTP {r.status_code}: {r.text[:300]}")
        body = r.json()
        text = body.get("text", "").strip()
        text = _dedupe_repetitions(text)
        text = _strip_hallucination_trails(text)
        detected_lang = body.get("language") or lang
        return text, detected_lang


service = TranscriptionService()
