import httpx
from .config import settings


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
        detected_lang = body.get("language") or lang
        return text, detected_lang


service = TranscriptionService()
