"""Application operations over the shared save, turn, and game connection."""

from __future__ import annotations

import asyncio
import uuid
from collections.abc import Awaitable, Callable
from typing import TYPE_CHECKING

from stardew_ai_runtime.companion_milestones import MilestoneError
from stardew_ai_runtime.protocol import Envelope, LifeMemoryEditPayload, LifeProfileSetPayload

if TYPE_CHECKING:
    from stardew_ai_runtime.adapters.game_bridge.websocket import WebSocketClient
    from stardew_ai_runtime.chat_bridge import ChatBridge


class PlayerOperationError(RuntimeError):
    pass


class PlayerService:
    def __init__(self, runtime: ChatBridge):
        self._runtime = runtime

    @property
    def game_session(self) -> str | None:
        return getattr(self._runtime, "_player_session_id", None)

    @property
    def game_connection(self) -> WebSocketClient | None:
        return self._runtime._chat_ws if self._runtime._latest_snapshot_payload else None

    def usage(self, save_id: str | None) -> dict:
        return self._runtime._usage_display.overview(save_id)

    def memory(self, save_id: str) -> dict:
        return self._runtime._memory_store.list(save_id)

    def work_status(self, save_id: str) -> tuple[dict, dict, list, bool]:
        runtime = self._runtime
        actor = (runtime._latest_snapshot_payload or {}).get("companion") or {}
        work = runtime._life_work_projection(save_id)
        milestones = runtime._build_milestones_state(save_id, "").payload["nodes"]
        thinking = runtime._busy_lock.locked() or runtime._active_task is not None
        return actor, work, milestones, thinking

    def partner_status(self) -> dict:
        return self._runtime._connection_projection()

    def current_partner_binding(self, binding: dict) -> bool:
        return (binding.get("bindingId"), binding.get("enabled", True)) == self._runtime._external_binding_id

    def partner_disconnecting(self) -> None:
        self._runtime._external_status = "waiting"
        self._runtime.abort_active_task("Desktop original partner disconnected")

    async def partner_attached(self, *, check_now: bool) -> None:
        self._runtime._external_health_at = 0
        if check_now:
            await self._runtime._refresh_external_connection(None)

    def pause_for_recovery(self, save_id: str) -> None:
        self._runtime._work_store.set_paused(save_id, True)
        self._runtime._autonomy.set_paused(save_id, True)

    def native_cancel_confirmed(self, save_id: str) -> None:
        self._runtime._pending_native_cancels.pop(save_id, None)

    async def cancel_turn_for_replacement(self, ws: WebSocketClient, save_id: str) -> None:
        runtime = self._runtime
        runtime._autonomy_generation += 1
        runtime._life_control_generation[save_id] = runtime._life_control_generation.get(save_id, 0) + 1
        await runtime.handle_chat_cancel(ws, None, "Superseded by player", save_id)
        try:
            async with asyncio.timeout(6):
                async with runtime._busy_lock:
                    pass
        except TimeoutError as error:
            raise PlayerOperationError("上一轮模型尚未退出，已停止派发。") from error

    async def control(
        self, ws: WebSocketClient, save_id: str, action: str,
        *, before_current_cancel: Callable[[], Awaitable[None]] | None = None, **parameters,
    ) -> bool:
        envelope = Envelope.create_autonomy_control(self._runtime.instance_id, uuid.uuid4().hex, save_id, action, **parameters)
        return await self._runtime._handle_autonomy_control(
            ws, envelope, save_id, before_current_cancel=before_current_cancel)

    def control_utterance(self, text: str) -> str | None:
        return self._runtime._control_utterance(text)

    async def submit_message(
        self, ws: WebSocketClient, save_id: str, request_id: str, text: str, *, decision: str | None,
    ) -> None:
        await self._runtime._handle_life_message(ws, "life.chat.submit", {
            "requestId": request_id, "saveId": save_id, "mode": "chat", "text": text, "source": "desktop",
            **({"replyToNoticeId": decision, "resolveNotice": True} if decision else {}),
        }, save_id)

    async def report_failed_command(self, ws: WebSocketClient, save_id: str, request_id: str, error: str) -> None:
        runtime = self._runtime
        reply = Envelope.create_life_chat_reply(runtime.instance_id, request_id, save_id, status="failed",
            profile_revision=runtime._profile_revision(save_id), memory_revision=runtime._memory_revision(save_id), error=error)
        await runtime._send_reply(ws, reply)

    def preferences(self, save_id: str) -> dict:
        runtime = self._runtime
        state = runtime._autonomy.state(save_id)
        return {**runtime._profile_store.get(save_id), "initiativeMode": state.mode, "idlePreference": state.idle_preference}

    def initiative_changes(self, save_id: str, mode: str, preference: str) -> list[tuple[str, dict]]:
        state = self._runtime._autonomy.state(save_id)
        return [(action, params) for action, change, params in (
            ("set_preferences", state.idle_preference != preference, {"idle_preference": preference}),
            ("set_mode", state.mode != mode, {"mode": mode}),
        ) if change]

    async def edit_memory(self, ws: WebSocketClient, save_id: str, body: dict) -> dict:
        runtime = self._runtime
        payload = LifeMemoryEditPayload.from_mapping({**body, "requestId": uuid.uuid4().hex, "saveId": save_id})
        status, result, entries, revision = runtime._apply_life_memory_edit(payload, save_id)
        if status != "confirmed":
            messages = {"STALE_REVISION": "记忆已变化，请重新读取后再编辑。", "NOT_FOUND": "这条记忆已不存在。",
                        "CANNOT_EDIT_EVENT": "共同经历的原文来自游戏结果，可删除该条记录。"}
            raise PlayerOperationError(messages.get(result.get("reason"), "记忆未保存，请检查输入。"))
        reply = Envelope.create_life_memory_state(runtime.instance_id, payload.request_id, save_id,
                                                   entries, revision, status=status)
        await runtime._send_reply(ws, reply)
        return {"entries": entries, "memoryRevision": revision}

    async def save_profile(self, ws: WebSocketClient, save_id: str, revision: int, patch: dict) -> tuple[dict, bool]:
        runtime = self._runtime
        payload = LifeProfileSetPayload.from_mapping({
            "requestId": uuid.uuid4().hex, "saveId": save_id, "expectedRevision": revision, "patch": patch,
        })
        reply = runtime._set_life_profile(payload)
        return reply.payload, await runtime._send_reply(ws, reply)

    def has_pending_decision(self, save_id: str, decision: str) -> bool:
        return any(item.get("id") == decision for item in self._runtime._work_store.unresolved_player_notices(save_id))

    async def change_decision_visibility(self, ws: WebSocketClient, save_id: str, action: str, notice_id: str) -> None:
        try:
            self._runtime._work_store.set_notice_visibility(save_id, [notice_id], action == "dismiss", require_unanswered=True)
        except ValueError as error:
            raise PlayerOperationError("该事项已回复或已不存在，请查看最新状态。") from error
        await self._runtime._push_work_state(ws, save_id)

    async def change_milestone(
        self, ws: WebSocketClient, save_id: str, action: str, node_id: str, updated_at: float,
        still_current: Callable[[], bool],
    ) -> dict:
        runtime = self._runtime
        game_date, play_style = runtime._life_game_date_playstyle(save_id)
        nodes = runtime._milestone_store.merged_nodes(save_id, game_date, play_style)
        selected = next((node for node in nodes if node["id"] == node_id), None)
        if not selected or selected["updatedAt"] != updated_at:
            raise PlayerOperationError("计划已变化，请查看最新状态后再操作。")
        allowed = ("suggested", "deferred") if action == "adopt" else ("suggested", "adopted")
        if selected["status"] not in allowed:
            raise PlayerOperationError("当前计划状态不支持此操作。")
        try:
            if action == "adopt":
                _, before = runtime._adopt_preparation(save_id, node_id, updated_at)
                runtime._pending_life_preparation.setdefault(save_id, before)
            else:
                deferred = runtime._milestone_store.defer(save_id, node_id, work_store=runtime._work_store,
                    game_date=game_date, expected_updated_at=updated_at)
                if deferred.get("goalId") and runtime._autonomy.state(save_id).goal_scope == deferred["goalId"]:
                    runtime._autonomy.set_goal_scope(save_id, None)
        except MilestoneError as error:
            raise PlayerOperationError(str(error)) from error
        projection = runtime._build_milestones_state(save_id, "")
        await runtime._send_reply(ws, projection)
        await runtime._push_work_state(ws, save_id)
        if action == "adopt" and still_current():
            runtime._schedule_life_drain(ws)
        return projection.payload
