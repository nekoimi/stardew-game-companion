from __future__ import annotations

import asyncio
import logging
import subprocess
import time
from collections.abc import Callable
from dataclasses import dataclass, field
from typing import Any

logger = logging.getLogger(__name__)


@dataclass
class ActiveChatTask:
    request_id: str
    save_id: str
    prompt: str = ""
    command_id: str = ""
    start_max_idx: int = -1
    start_max_step_idx: int = -1
    wire_start_offset: int = 0
    process: subprocess.Popen | None = None
    async_task: asyncio.Task | None = None
    cancelled: bool = False
    abort_reason: str | None = None
    start_time: float = field(default_factory=time.monotonic)
    recorded: bool = False
    interrupted_result: dict[str, Any] | None = None
    decision_token: str | None = None
    decision_day: str | None = None
    provider_conversation_id: str | None = None
    instructions_text: str | None = None
    # Full original context is retained only for a provider's fresh-session recovery.
    provider_bootstrap_prompt: str | None = None
    execution_deferred: bool = False
    # Conversations retain their session while existing native work settles.
    preserve_session: bool = False
    goal_scope: str | None = None
    goal_default: str | None = None
    work_request_ids: list[str] = field(default_factory=list)
    delivered_speech: set[str] = field(default_factory=set)


def cancel_active_turn(
    task: ActiveChatTask | None,
    reason: str,
    backend_name: str,
    stop_owned_process: Callable[[subprocess.Popen], None],
) -> None:
    if task is None:
        return
    logger.info("Aborting active chat task [%s] (%s)", task.request_id, reason)
    task.cancelled = True
    task.abort_reason = reason
    process = task.process
    if process is not None and process.poll() is None:
        try:
            stop_owned_process(process)
            logger.info("Cancelled %s game turn (owned process pid=%s)", backend_name, getattr(process, "pid", None))
        except Exception as error:
            logger.debug("Error killing process: %s", error)
    if task.async_task is not None and not task.async_task.done():
        task.async_task.cancel()
