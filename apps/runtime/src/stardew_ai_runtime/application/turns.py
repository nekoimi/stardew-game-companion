from __future__ import annotations

import asyncio
import json
import logging
import os
import subprocess
import time
import uuid
from collections.abc import Callable
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any

from stardew_ai_runtime import compatibility

if TYPE_CHECKING:
    from stardew_ai_runtime.chat_bridge import ChatBridge

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


def execute_model_turn(
    runtime: ChatBridge,
    active_task: ActiveChatTask,
    conversation_id: str | None,
    prompt: str,
) -> dict[str, Any]:
    """Dispatch one turn to the configured provider adapter."""
    if not runtime._connection_ready():
        return {"success": False, "response": "等待原伙伴连接；未启动替代会话。",
                "conversation_id": conversation_id, "error": "EXTERNAL_CONNECTION_WAITING"}
    compatibility.assert_native_compatible(runtime.run_dir)
    token = uuid.uuid4().hex
    save_id = active_task.save_id
    if save_id and runtime._work_store is not None and not active_task.execution_deferred:
        scope = active_task.goal_scope or (runtime._autonomy.state(save_id).goal_scope
                 if runtime._autonomy and active_task.request_id.startswith("autonomy-") else None)
        state = runtime._work_store.state(save_id)
        goal = next((g for g in state.goals if g.id == scope), None)
        source_id = goal.constraints.get("playerRequestId") if goal else None
        source = next((r for r in state.requests if r.get("id") == source_id), None)
        if source:
            prompt += "\n玩家原始指令（保留动作限制、汇报要求与统计范围；当前已采纳范围以目标为准）：" + json.dumps(
                {"text": source["text"]}, ensure_ascii=False)
        runtime._work_store.begin_decision(
            save_id, token, goal_scope=scope, goal_default=active_task.goal_default,
            request_id=source_id if source else active_task.command_id or active_task.request_id,
            request_ids=list(dict.fromkeys([
                *active_task.work_request_ids,
                *(str(request["id"]) for request in runtime._work_store.pending_midflight_requests(save_id)
                  if not active_task.execution_deferred),
            ])),
        )
        active_task.decision_token = token
        active_task.decision_day = runtime._work_store.state(save_id).last_settled_day
    previous_token = os.environ.get("STARDEW_DECISION_TOKEN")
    previous_surface = os.environ.get("STARDEW_MCP_SURFACE")
    # Capability discovery stays fixed; only cached reads/execution admission differ.
    os.environ["STARDEW_MCP_SURFACE"] = "light"
    if active_task.execution_deferred:
        os.environ.pop("STARDEW_DECISION_TOKEN", None)
    else:
        os.environ["STARDEW_DECISION_TOKEN"] = token
    try:
        backend = runtime._get_backend()
        if hasattr(backend, "progress"):
            backend.progress = getattr(runtime, "_backend_progress_callback", None)
        turn_native_session = getattr(runtime, "_player_session_id", None)
        delivery_key, sent_prompt, pending_prompt = runtime._prepare_prompt_delivery(
            active_task, conversation_id, prompt, backend)
        result = backend.run(active_task, conversation_id, sent_prompt)
        if conversation_id and result.get("conversation_id") not in {None, conversation_id}:
            # A provider may return an unexpected session; never bless it as
            # continuity or advance the old conversation's delivery baseline.
            active_task.provider_input_delivered = False
            result.update(actual_provider_session_id=result.get("conversation_id"),
                          success=False, error="PROVIDER_SESSION_CHANGED",
                          response="后端返回了不同会话；原绑定保留，请检查客户端。", conversation_id=conversation_id)
        delivered_session = result.get("conversation_id")
        if delivered_session and (getattr(active_task, "provider_input_delivered", False) or (not active_task.cancelled and not result.get("error"))):
            runtime._acknowledge_prompt_delivery(delivery_key, pending_prompt, save_id, str(delivered_session))
            if runtime.actor_mode == "player":
                runtime._work_reload_context(active_task.save_id, str(delivered_session), delivered=True,
                                          expected_game_session_id=turn_native_session)
        if delivered_session and result.get("context_compacted"):
            runtime._prompt_delivery.invalidate_session(delivery_key, str(delivered_session))
        logger.info("Prompt delivery save=%s session=%s version=%s originalBytes=%s newBytes=%s",
                    active_task.save_id, delivered_session, pending_prompt["version"],
                    pending_prompt["originalPromptBytes"], pending_prompt["newPromptBytes"])
        return result
    finally:
        if previous_surface is None:
            os.environ.pop("STARDEW_MCP_SURFACE", None)
        else:
            os.environ["STARDEW_MCP_SURFACE"] = previous_surface
        if previous_token is None:
            os.environ.pop("STARDEW_DECISION_TOKEN", None)
        else:
            os.environ["STARDEW_DECISION_TOKEN"] = previous_token
