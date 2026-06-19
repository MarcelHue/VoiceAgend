import os
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI
from fastapi.staticfiles import StaticFiles
from fastapi.responses import FileResponse
from sqlalchemy import select

from .config import settings
from .db import init_db, SessionLocal, User
from .security import hash_password
from .transcribe import service
from .routes_auth import router as auth_router
from .routes_users import router as users_router
from .routes_ws import router as ws_router
from .routes_stats import router as stats_router
from .routes_profile import router as profile_router
from .routes_web import router as web_router


async def ensure_default_admin() -> None:
    async with SessionLocal() as db:
        # .first() statt .scalar_one_or_none() — wir prüfen nur ob >=1 Admin existiert.
        # scalar_one_or_none() wirft MultipleResultsFound, wenn schon mehrere Admins angelegt sind.
        res = await db.execute(select(User).where(User.is_admin == True).limit(1))
        if res.first() is not None:
            return
        username = os.getenv("VOICEAGEND_ADMIN_USER", "admin")
        password = os.getenv("VOICEAGEND_ADMIN_PASSWORD", "admin")
        admin = User(username=username, password_hash=hash_password(password), is_admin=True)
        db.add(admin)
        await db.commit()
        print(f"[init] created default admin user '{username}' — change the password!")


@asynccontextmanager
async def lifespan(app: FastAPI):
    await init_db()
    await ensure_default_admin()
    print(f"[init] whisper backend: {settings.whisper_url} (model={settings.whisper_model})")
    await service.start()
    yield
    await service.stop()


app = FastAPI(title="VoiceAgend Server", version="0.2.0", lifespan=lifespan)

app.include_router(auth_router)
app.include_router(users_router)
app.include_router(stats_router)
app.include_router(profile_router)
app.include_router(web_router)
app.include_router(ws_router)


@app.get("/api/health")
async def health():
    backend = await service.health()
    return {
        "status": "ok",
        "whisper_url": settings.whisper_url,
        "whisper_model": settings.whisper_model,
        "whisper_backend": backend,
    }


WEB_DIR = Path(__file__).resolve().parent.parent / "web"
if WEB_DIR.exists():
    app.mount("/static", StaticFiles(directory=WEB_DIR / "static"), name="static")

    @app.get("/")
    async def index():
        return FileResponse(WEB_DIR / "index.html")

    @app.get("/app")
    async def record_app():
        """Aufnahme-/Transkriptions-PWA (separate Seite vom Admin-Dashboard)."""
        return FileResponse(WEB_DIR / "app.html")

    @app.get("/sw.js")
    async def service_worker():
        # Muss vom Root ausgeliefert werden, damit der Scope "/" die /app-Seite abdeckt.
        return FileResponse(WEB_DIR / "sw.js", media_type="application/javascript")

    @app.get("/manifest.webmanifest")
    async def manifest():
        return FileResponse(
            WEB_DIR / "manifest.webmanifest", media_type="application/manifest+json"
        )
