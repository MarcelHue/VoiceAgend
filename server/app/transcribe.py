import re
from difflib import SequenceMatcher

import httpx
from .config import settings


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
    """
    Collapses runs of identical or near-identical consecutive sentences that
    typically come from Whisper hallucinations at silence boundaries.

    Example input:  "A. B. B. B. B. B. B."
    Example output: "A. B."

    A legitimate intentional double ("Nein. Nein.") is preserved — only runs
    of `min_repeat` or more identical sentences are reduced.
    """
    if not text or not text.strip():
        return text
    # Split by sentence-ending punctuation, preserving the marker
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
            out.append(current)  # keep one
            i = j
        else:
            # Keep all instances (could be intentional <min_repeat repetition)
            out.extend(parts[i:j])
            i = j
    return " ".join(out)


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
    ) -> tuple[str, str | None]:
        assert self._client is not None
        files = {"file": ("audio.ogg", audio_bytes, "audio/ogg")}
        data: dict[str, str] = {
            "model": model or settings.whisper_model,
            "response_format": "json",
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
        detected_lang = body.get("language") or lang
        return text, detected_lang


service = TranscriptionService()
