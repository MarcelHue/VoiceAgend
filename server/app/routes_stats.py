from typing import Annotated
from datetime import datetime, timedelta
from fastapi import APIRouter, Depends
from pydantic import BaseModel
from sqlalchemy import select, func
from sqlalchemy.ext.asyncio import AsyncSession

from .db import get_session, Transcription, User
from .security import get_current_user, require_admin

router = APIRouter(prefix="/api/stats", tags=["stats"])


class StatsResponse(BaseModel):
    total_transcriptions: int
    total_audio_seconds: float
    total_processing_seconds: float
    last_24h: int


@router.get("/me", response_model=StatsResponse)
async def my_stats(
    user: Annotated[User, Depends(get_current_user)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    return await _stats(db, user_id=user.id)


@router.get("/global", response_model=StatsResponse)
async def global_stats(
    _: Annotated[User, Depends(require_admin)],
    db: Annotated[AsyncSession, Depends(get_session)],
):
    return await _stats(db, user_id=None)


async def _stats(db: AsyncSession, user_id: int | None) -> StatsResponse:
    q = select(
        func.count(Transcription.id),
        func.coalesce(func.sum(Transcription.duration_ms), 0),
        func.coalesce(func.sum(Transcription.processing_ms), 0),
    )
    if user_id is not None:
        q = q.where(Transcription.user_id == user_id)
    res = await db.execute(q)
    count, dur_ms, proc_ms = res.one()

    cutoff = datetime.utcnow() - timedelta(hours=24)
    q24 = select(func.count(Transcription.id)).where(Transcription.created_at >= cutoff)
    if user_id is not None:
        q24 = q24.where(Transcription.user_id == user_id)
    last24 = (await db.execute(q24)).scalar_one()

    return StatsResponse(
        total_transcriptions=count,
        total_audio_seconds=dur_ms / 1000,
        total_processing_seconds=proc_ms / 1000,
        last_24h=last24,
    )
