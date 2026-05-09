from pathlib import Path
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", env_prefix="VOICEAGEND_", extra="ignore")

    data_dir: Path = Path("/data")
    default_language: str | None = None
    jwt_secret: str = "change-me-in-production"
    jwt_alg: str = "HS256"
    jwt_ttl_minutes: int = 60 * 24
    max_upload_mb: int = 100

    # Whisper-Backend (eigener Speaches-Container)
    whisper_url: str = "http://speaches:8000"
    whisper_model: str = "Systran/faster-whisper-medium"
    whisper_api_key: str | None = None
    whisper_timeout_s: int = 300

    @property
    def db_path(self) -> Path:
        self.data_dir.mkdir(parents=True, exist_ok=True)
        return self.data_dir / "voiceagend.db"


settings = Settings()
