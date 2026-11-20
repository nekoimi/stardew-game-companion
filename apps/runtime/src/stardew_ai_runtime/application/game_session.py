"""Apply a native save reload before admitting new game turns."""

from __future__ import annotations

import asyncio
import logging
from typing import TYPE_CHECKING

from stardew_ai_runtime.game_reload import (
    mark_game_load_applied,
    pending_game_load,
    preserve_provider_sessions,
    recover_recorded_choices,
    restore_game_load,
)
from stardew_ai_runtime.prompt_delivery import PromptDelivery

if TYPE_CHECKING:
    from stardew_ai_runtime.chat_bridge import ChatBridge

logger = logging.getLogger(__name__)


async def restore_loaded_game(runtime: ChatBridge, save_id: str | None, session_id: str | None) -> None:
    if runtime.run_dir is None or not save_id:
        return
    request = pending_game_load(runtime.run_dir, save_id, session_id)
    if request is None:
        recover_recorded_choices(runtime.run_dir, save_id)
        return
    runtime.abort_active_task("Game save reloaded")
    runtime._autonomy_generation += 1
    runtime._active_player_requests.pop(save_id, None)
    delivery = runtime._delivery_tasks.pop(save_id, None)
    if delivery is not None and not delivery.done():
        delivery.cancel()
    wakeup = runtime._autonomy_wakeup_tasks.pop(save_id, None)
    if wakeup is not None and wakeup is not asyncio.current_task():
        wakeup.cancel()
        await asyncio.gather(wakeup, return_exceptions=True)
    runtime._autonomy_wakeup_snapshots.pop(save_id, None)
    if runtime._plan_worker is not None:
        await runtime._plan_worker.stop()
    active = runtime._active_task.async_task if runtime._active_task else None
    if active is not None and active is not asyncio.current_task():
        await asyncio.gather(active, return_exceptions=True)
    restore_game_load(runtime.run_dir, request)
    preserve_provider_sessions(runtime.run_dir, request, runtime._sessions, runtime._sessions_file)
    delivery = PromptDelivery(runtime.run_dir / "prompt-delivery.json")
    for key, baseline in list(delivery.state.items()):
        if f":{save_id}:" in key:
            delivery.invalidate_session(key, baseline.get("session"))
    runtime._latest_snapshot_payload = None

    runtime._latest_snapshot_observed_at = None
    runtime._latest_snapshot_revision = None
    runtime._current_game_day_key = None
    runtime._last_day_settlement = None
    runtime._last_native_ui_recovery_signal = None
    runtime._evening_care_fired_day = None
    for mapping in (runtime._last_work_projection, runtime._work_projection_times,
                    runtime._pending_native_cancels, runtime._autonomy_last_snapshot_at,
                    runtime._autonomy_pending_snapshot, runtime._autonomy_pending_task_fingerprints):
        mapping.pop(save_id, None)
    runtime._life_proposals.pop(save_id, None)
    runtime._bind_autonomy_store()
    runtime._bind_work_store()
    runtime._bind_companion_stores()
    recover_recorded_choices(runtime.run_dir, save_id)
    if runtime._life_chat is not None:
        runtime._life_chat.record_fingerprint(save_id, runtime._profile_revision(save_id), runtime._memory_instruction_revision(save_id))
    mark_game_load_applied(runtime.run_dir, request)
    logger.info("Restored game-save facts with retained player-intent summary for %s", save_id)
