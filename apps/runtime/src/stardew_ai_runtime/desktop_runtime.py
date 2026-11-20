"""Runtime operations exposed to desktop and local command clients."""

from __future__ import annotations

import asyncio
import json
import logging
import os
import uuid
from collections import deque
from pathlib import Path
from typing import Any

from .chat_bridge import ChatBridge, Envelope
from .companion_milestones import MilestoneError
from .desktop_partner import DesktopPartner
from .external_connection import AcpError, read_binding
from .protocol import LifeMemoryEditPayload, LifeProfileSetPayload
from .provider_registry import PROVIDERS, ClientError

log = logging.getLogger(__name__)


class DesktopError(RuntimeError):
    pass


class DesktopBridge(ChatBridge):
    _local_pi_sessions_only = True

    def __init__(self, run_dir: Path, model: str, client: str, *, backend: str = "pi", effort: str = ""):
        provider = PROVIDERS[backend]
        if effort and effort not in provider.efforts:
            raise DesktopError(f"{provider.name} 不支持所选推理档位。")
        super().__init__(run_dir=str(run_dir), backend=backend, model=model,
                         effort=effort or provider.default_effort,
                         enable_plan_worker=False, backend_command=client)
        self.save: str | None = None
        self.game_session: str | None = None
        self.events: deque[tuple[int, dict]] = deque(maxlen=300)
        self.sequence = 0
        self.requests: dict[str, tuple[tuple, dict]] = {}
        self.controls: dict[str, asyncio.Future] = {}
        self.turns: set[asyncio.Task] = set()
        self.operation = asyncio.Lock()
        self._desktop_control_pending = False
        self.native_state: dict = {}
        self._awaiting_native_control = True
        self.settling: str | None = None
        self.partner = DesktopPartner(run_dir)

    def publish(self, kind: str, payload: dict) -> None:
        self.sequence += 1
        self.events.append((self.sequence, {"type": kind, "payload": payload}))

    def command_result(self, request_id: str) -> dict | None:
        entry = self.requests.get(request_id)
        return entry[1] if entry else None

    def events_after(self, cursor: int) -> list[tuple[int, dict]]:
        return [(index, event) for index, event in self.events if index > cursor]

    def usage(self, game: str | None) -> dict:
        save = None
        if game is not None:
            save, session, _ = self.binding()
            if game != session:
                raise DesktopError("存档已变化，请刷新用量。")
        return self._usage_display.overview(save)

    def memory(self, binding) -> dict:
        return self._memory_store.list(binding[0])

    def submit_command(self, binding, request_id: str, text: str, mode: str, decision: str | None) -> tuple[dict, bool]:
        signature = (text, mode, decision)
        if current := self.requests.get(request_id):
            if signature != current[0]:
                raise DesktopError("此请求编号已用于另一条指令。")
            return current[1], False
        if self._awaiting_native_control or self.native_state.get("recoveryPending"):
            raise DesktopError("请先等待游戏状态同步，或核对旧动作后继续。")
        if decision and not self.has_pending_decision(decision):
            raise DesktopError("该决定已结束，请刷新后重试。")
        if len(self.turns) >= 8:
            raise DesktopError("待处理指令已满，请稍后再试。")
        if len(self.requests) >= 2048:
            raise DesktopError("本次连接的指令记录已满，请在保存后重新连接。")
        if not self._control_utterance(text) or decision:
            self.require_partner()
        result = {"requestId": request_id, "status": "accepted"}
        self.requests[request_id] = (signature, result)
        self.publish("submitted", {**result, "text": text})
        task = asyncio.create_task(self.command(binding, request_id, text, mode == "interrupt", decision))
        self.turns.add(task)
        task.add_done_callback(self.turns.discard)
        return result, True

    async def _prepare_game_load(self, save_id, session_id):
        await super()._prepare_game_load(save_id, session_id)
        if session_id != self.game_session:
            self.game_session = session_id
            self.requests.clear()
            self.events.clear()
            self.settling = None

    async def _receive_loop(self, ws, active_save_id, stop_event=None):
        self._latest_snapshot_payload = None
        self.save = active_save_id
        self.native_state = {}
        self._awaiting_native_control = True
        try:
            await super()._receive_loop(ws, active_save_id, stop_event)
        finally:
            self.save = None
            for task in tuple(self.turns):
                task.cancel()
            await asyncio.gather(*self.turns, return_exceptions=True)
            for _, result in self.requests.values():
                if result["status"] in {"accepted", "queued", "processing"}:
                    result.update(status="unknown", error="连接中断，指令结果尚未确认。")
                    self.publish("life.chat.reply", result.copy())

    async def _maybe_claim_player_control(self, ws, save_id):
        self._player_claim_attempted = True  # Desktop handoff requires an explicit button.

    async def _send_reply(self, ws, reply):
        if reply.message_type in {"chat.reply", "life.chat.reply", "life.care"} and reply.payload.get("saveId") == self.save:
            self.publish(reply.message_type, reply.payload)
            if entry := self.requests.get(reply.payload.get("requestId", "")):
                entry[1].update(reply.payload)
        return await super()._send_reply(ws, reply)

    def desktop_message(self, envelope: dict, active_save_id: str | None) -> None:
        if envelope.get("saveId") != active_save_id or envelope.get("gameSessionId") != self._player_session_id:
            return
        payload = envelope.get("payload") or {}
        if envelope["messageType"] == "desktop.result":
            self.publish("result", payload)
            return
        if "paused" in payload:
            self.native_state = payload
            self._awaiting_native_control = False
        future = self.controls.get(payload.get("requestId"))
        if future is not None and not future.done():
            future.set_result(payload)

    def binding(self) -> tuple[str, str, Any]:
        ws = getattr(self, "_chat_ws", None)
        session = getattr(self, "_player_session_id", None)
        if not self.save or not session or session != self.game_session or not self._latest_snapshot_payload or ws is None:
            raise DesktopError("请先启动游戏并进入存档，等待伙伴连接。")
        return self.save, session, ws

    def _connection_ready(self) -> bool:
        return not (self._awaiting_native_control or self.native_state.get("recoveryPending")) and self._partner_available()

    def _partner_available(self) -> bool:
        if not super()._connection_ready():
            return False
        if self.connection_source != "external":
            return True
        binding = read_binding(self.run_dir)
        return bool(binding and (binding.get("bindingId"), binding.get("enabled", True)) == self._external_binding_id)

    def connection_status(self) -> dict:
        state = self._connection_projection()
        ready = self._connection_ready()
        if self.connection_source == "builtin":
            state["clientName"] = PROVIDERS[self.backend_name].name
        elif not self._partner_available() and state["status"] == "connected":
            state["status"] = "waiting"
        return {**state, "ready": ready, "ownedAdapter": self.partner.active()}

    def require_partner(self) -> None:
        if self._awaiting_native_control or self.native_state.get("recoveryPending"):
            raise DesktopError("请先等待游戏状态同步，或核对旧动作后继续。")
        if not self._connection_ready():
            raise DesktopError("原伙伴尚未连接，请在设置中恢复原会话。")

    def status(self) -> dict:
        connection = self.connection_status()
        try:
            save, session, _ = self.binding()
        except DesktopError:
            return {"ready": False, "connection": connection}
        snap = self._latest_snapshot_payload or {}
        actor = snap.get("companion") or {}
        work = self._life_work_projection(save)
        return {
            "ready": True, "saveId": save, "gameSessionId": session, "connection": connection,
            "actorMode": self.actor_mode, "controlOwner": self.control_owner,
            "paused": bool(work.get("paused") or self.native_state.get("paused")),
            "interrupting": bool(self._desktop_control_pending or self.native_state.get("interrupting")),
            "recoveryPending": bool(self.native_state.get("recoveryPending")),
            "thinking": bool(self._busy_lock.locked() or self._active_task),
            "activity": self.native_state.get("activity", actor.get("activity")),
            "activeCommandId": self.native_state.get("activeCommandId", actor.get("activeCommandId")),
            "progress": self.native_state.get("progress"),
            "clockPauseOwner": self.native_state.get("clockPauseOwner"),
            "inboxRevision": self.native_state.get("inboxRevision"),
            "unreadMessages": self.native_state.get("unreadMessages", 0),
            "work": {key: work.get(key) for key in (
                "currentGoalId", "activeGoals", "recentTodos", "recentExecutions", "activity",
                "waitingConditions", "pauseReason",
            )},
            "decisions": [d for d in work.get("playerDecisions", []) or [] if d.get("status") == "pending"],
            "closedDecisions": [d for d in work.get("playerDecisions", []) or [] if d.get("status") == "dismissed"],
            "milestones": self._build_milestones_state(save, "").payload["nodes"],
        }

    async def native_control(self, binding, action: str, *, request_id: str | None = None, **params) -> dict:
        save, session, ws = binding
        if binding != self.binding():
            raise DesktopError("游戏会话已变化，请重新发送。")
        request_id = request_id or uuid.uuid4().hex
        future = asyncio.get_running_loop().create_future()
        self.controls[request_id] = future
        try:
            await asyncio.wait_for(ws.send_text(json.dumps({
                "messageType": "desktop.control", "saveId": save, "gameSessionId": session,
                "payload": {"requestId": request_id, "action": action, **params},
            }, ensure_ascii=False)), 3)
            reply = await asyncio.wait_for(future, 4)
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新发送。")
            if reply.get("error"):
                if reply["error"] == "HISTORY_UNAVAILABLE":
                    raise DesktopError("当前存档的对话记录暂时无法读取。")
                if reply["error"] == "MESSAGE_NOT_AVAILABLE":
                    raise DesktopError("这条消息已移出可标记的范围，请刷新提醒。")
                if reply["error"] == "TERMINAL_UNKNOWN":
                    raise DesktopError("游戏找不到旧动作的终态回执，已保持暂停。请在游戏中核对后保存并重新进入存档。")
                if reply["error"] == "ACTION_STILL_RUNNING" and action == "recover":
                    raise DesktopError("旧动作仍在停止，请稍候再核对。")
                if reply["error"] == "RECOVERY_PENDING":
                    raise DesktopError("请先核对旧动作。")
                raise DesktopError("游戏未确认操作：" + reply["error"])
            return reply
        except TimeoutError as ex:
            raise DesktopError("游戏暂未确认控制请求，请切回游戏检查状态。") from ex
        finally:
            self.controls.pop(request_id, None)

    async def settle(self, binding):
        until = asyncio.get_running_loop().time() + 8
        while asyncio.get_running_loop().time() < until:
            reply = await self.native_control(binding, "status", commandId=self.settling)
            self.settling = self.settling or reply.get("interruptedCommandId")
            if not reply["active"] and (self.settling is None or reply.get("result")):
                self.settling = None
                return
            await asyncio.sleep(0.15)
        raise DesktopError("旧动作终态尚未确认，已停止派发。可重试「继续」核对结果。")

    async def interrupt(self, binding) -> None:
        save, _, ws = binding
        held = await self.native_control(binding, "interrupt")
        self.settling = held.get("interruptedCommandId") or self.settling
        self._autonomy_generation += 1
        self._life_control_generation[save] = self._life_control_generation.get(save, 0) + 1
        await self.handle_chat_cancel(ws, None, "Superseded by player", save)
        try:
            async with asyncio.timeout(6):
                async with self._busy_lock:
                    pass
        except TimeoutError as ex:
            raise DesktopError("上一轮模型尚未退出，已停止派发。") from ex
        await self.settle(binding)
        self._pending_native_cancels.pop(save, None)

    async def control(self, binding, action: str, **params) -> None:
        save, _, ws = binding
        if action == "takeover":
            await self.native_control(binding, action)
            return
        async with self.operation:
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新发送。")
            if action == "recover":
                if self.native_state.get("recoveryPending"):
                    self._work_store.set_paused(save, True)
                    self._autonomy.set_paused(save, True)
                else:
                    raise DesktopError("当前没有待核对的断线动作。")
                reply = await self.native_control(binding, "recover")
                self.settling = None
                self._pending_native_cancels.pop(save, None)
                if receipt := reply.get("result"):
                    self.publish("result", receipt)
                self.publish("recovery", {"hasReceipt": bool(reply.get("result"))})
                return
            if self._awaiting_native_control or self.native_state.get("recoveryPending"):
                raise DesktopError("请先核对旧动作。")
            self._desktop_control_pending = True
            held_native = False

            async def hold_for_cancel():
                nonlocal held_native
                held = await self.native_control(binding, "interrupt")
                held_native = True
                self.settling = held.get("interruptedCommandId") or self.settling

            try:
                if action == "handoff":
                    self.require_partner()
                    await self.native_control(binding, action)
                    return
                if action == "pause":
                    await self.native_control(binding, "pause")
                if action == "resume":
                    if (await self.native_control(binding, "status")).get("interrupting"):
                        await self.settle(binding)
                        self._pending_native_cancels.pop(save, None)
                        await self.native_control(binding, "unhold")
                targeted_cancel = action == "cancel" and bool(params.get("goalId"))
                if action == "cancel" and not targeted_cancel:
                    await hold_for_cancel()
                confirmed = await self._handle_autonomy_control(
                    ws, Envelope.create_autonomy_control(self.instance_id, uuid.uuid4().hex, save, action, **params), save,
                    before_current_cancel=hold_for_cancel if targeted_cancel else None)
                if held_native:
                    await self.settle(binding)
                    self._pending_native_cancels.pop(save, None)
                    await self.native_control(binding, "unhold")
                if not confirmed and not held_native:
                    raise DesktopError("运行时尚未确认操作。")
                if action == "resume":
                    await self.native_control(binding, "resume")
            finally:
                self._desktop_control_pending = False

    def preferences(self, save: str) -> dict:
        state = self._autonomy.state(save)
        return {**self._profile_store.get(save), "initiativeMode": state.mode, "idlePreference": state.idle_preference}

    async def edit_memory(self, binding, body: dict) -> dict:
        async with self.operation:
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新读取记忆与约定。")
            save, _, ws = binding
            payload = LifeMemoryEditPayload.from_mapping({**body, "requestId": uuid.uuid4().hex, "saveId": save})
            status, result, entries, revision = self._apply_life_memory_edit(payload, save)
            if status != "confirmed":
                message = {"STALE_REVISION": "记忆已变化，请重新读取后再编辑。", "NOT_FOUND": "这条记忆已不存在。",
                           "CANNOT_EDIT_EVENT": "共同经历的原文来自游戏结果，可删除该条记录。"}
                raise DesktopError(message.get(result.get("reason"), "记忆未保存，请检查输入。"))
            reply = Envelope.create_life_memory_state(self.instance_id, payload.request_id, save,
                                                      entries, revision, status=status)
            await self._send_reply(ws, reply)
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新读取记忆与约定。")
            return {"entries": entries, "memoryRevision": revision}

    async def save_profile(self, binding, revision: int, patch: dict) -> dict:
        async with self.operation:
            if self.connection_source == "external":
                raise DesktopError("原伙伴的人格与角色设置由原客户端管理。")
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新读取伙伴设置。")
            save, _, ws = binding
            payload = LifeProfileSetPayload.from_mapping({
                "requestId": uuid.uuid4().hex, "saveId": save, "expectedRevision": revision, "patch": patch,
            })
            reply = self._set_life_profile(payload)
            delivered = await self._send_reply(ws, reply)
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新读取伙伴设置。")
            if reply.payload.get("reason") == "STALE_REVISION":
                raise DesktopError("伙伴设置已变化，请重新读取后再保存。")
            if reply.payload.get("status") != "confirmed":
                raise DesktopError("伙伴设置未保存，请检查填写的内容。")
            if not delivered:
                raise DesktopError("设置已写入存档，游戏尚未确认接收。")
            return self.preferences(save)

    async def external_partner(self, body: dict) -> dict:
        if self.connection_source != "external":
            raise DesktopError("请先选择「接续原 MiniMax Code 会话」并重新启动。")
        action = body.get("action")
        if action == "disconnect":
            if not self.partner.active():
                raise DesktopError("此连接由原客户端启动，请在那里断开。")
            self._external_status = "waiting"
            self.abort_active_task("Desktop original partner disconnected")
            await self.partner.stop()
            return {"disconnected": True}
        cwd, data_dir, client, session, name = (body.get(key, "") for key in ("cwd", "dataDir", "client", "sessionId", "name"))
        if (action not in ("list", "attach") or any(not isinstance(value, str) or "\0" in value
                for value in (cwd, data_dir, client, session, name)) or len(session) > 200 or len(name) > 80):
            raise DesktopError("请选择有效的原工作目录和会话。")
        if action == "attach":
            if not session or body.get("originalClientIdle") is not True:
                raise DesktopError("请选择原会话，并确认原聊天窗口保持闲置。")
            old = read_binding(self.run_dir)
            if self.save and old:
                selected_data = data_dir or os.environ.get("MINIMAX_DATA_DIR")
                if (old["providerSessionId"] != session or Path(old["cwd"]).resolve() != Path(cwd).resolve()
                        or old.get("dataDir") != (str(Path(selected_data).resolve()) if selected_data else None)):
                    raise DesktopError("更换原会话前，请保存并回到游戏标题画面。")
        try:
            shell = PROVIDERS["mcode"].find_client(client)
            result = await self.partner.launch(cwd=cwd, data_dir=data_dir, command=shell,
                                               session=session if action == "attach" else "", name=name)
        except (AcpError, ClientError) as ex:
            raise DesktopError(str(ex)) from ex
        if action == "attach":
            self._external_health_at = 0
            if not self.save:
                await self._refresh_external_connection(None)
        else:
            result["sessions"] = [
                {"sessionId": row["sessionId"], "title": row.get("title") or ""}
                for row in result["sessions"] if isinstance(row, dict) and isinstance(row.get("sessionId"), str)
            ]
        return result

    async def save_initiative(self, binding, mode: str, preference: str) -> dict:
        async with self.operation:
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新读取伙伴设置。")
            save, _, ws = binding
            state = self._autonomy.state(save)
            for action, changed, params in (
                ("set_preferences", state.idle_preference != preference, {"idle_preference": preference}),
                ("set_mode", state.mode != mode, {"mode": mode}),
            ):
                if changed and not await self._handle_autonomy_control(ws, Envelope.create_autonomy_control(
                        self.instance_id, uuid.uuid4().hex, save, action, **params), save):
                    raise DesktopError("主动帮忙设置尚未确认，请重新读取。")
                if binding != self.binding():
                    raise DesktopError("游戏会话已变化，请重新读取伙伴设置。")
            return self.preferences(save)

    async def command(self, binding, request_id: str, text: str, interrupt: bool, decision: str | None):
        save, _, ws = binding
        try:
            action = self._control_utterance(text)
            if action and decision is None:
                from .work_controls import explicit_cancel_scope
                params = {"allArrangements": True} if explicit_cancel_scope(text) == "all" else {}
                await self.control(binding, action, **params)
                self.publish("chat.reply", {"requestId": request_id, "status": "completed", "replyText": "控制操作已确认。"})
                self.requests[request_id][1]["status"] = "completed"
                return
            async with self.operation:
                self.require_partner()
                if decision is not None and not self.has_pending_decision(decision):
                    raise DesktopError("该决定已结束，请刷新后重试。")
                if interrupt:
                    self._desktop_control_pending = True
                try:
                    if interrupt:
                        if self.status().get("paused") or self.actor_mode == "player" and self.control_owner != "agent":
                            raise DesktopError("当前处于暂停或玩家控制状态，请先在桌面恢复。")
                        self.publish("command", {"requestId": request_id, "status": "interrupting"})
                        await self.interrupt(binding)
                    await self.native_control(binding, "record", request_id=request_id, text=text, decisionId=decision)
                    if interrupt:
                        await self.native_control(binding, "unhold")
                finally:
                    self._desktop_control_pending = False
            if binding != self.binding():
                raise DesktopError("存档已切换，这条指令未派发。")
            await self._handle_life_message(ws, "life.chat.submit", {
                "requestId": request_id, "saveId": save, "mode": "chat", "text": text, "source": "desktop",
                **({"replyToNoticeId": decision, "resolveNotice": True} if decision else {}),
            }, save)
        except asyncio.CancelledError:
            raise
        except Exception as ex:
            if not isinstance(ex, DesktopError):
                log.exception("Desktop command failed")
            error = str(ex) if isinstance(ex, DesktopError) else "连接异常，指令结果未确认。"
            reply = Envelope.create_life_chat_reply(self.instance_id, request_id, save, status="failed",
                profile_revision=self._profile_revision(save), memory_revision=self._memory_revision(save), error=error)
            await self._send_reply(ws, reply)

    def has_pending_decision(self, decision: str) -> bool:
        save, _, _ = self.binding()
        return any(item.get("id") == decision for item in self._work_store.unresolved_player_notices(save))

    async def change_decision_visibility(self, binding, action: str, notice_id: str) -> None:
        async with self.operation:
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新操作。")
            save, _, ws = binding
            try:
                self._work_store.set_notice_visibility(save, [notice_id], action == "dismiss", require_unanswered=True)
            except ValueError as ex:
                raise DesktopError("该事项已回复或已不存在，请查看最新状态。") from ex
            await self._push_work_state(ws, save)

    async def change_milestone(self, binding, action: str, node_id: str, updated_at: float) -> dict:
        async with self.operation:
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新操作。")
            save, _, ws = binding
            game_date, play_style = self._life_game_date_playstyle(save)
            nodes = self._milestone_store.merged_nodes(save, game_date, play_style)
            selected = next((node for node in nodes if node["id"] == node_id), None)
            if not selected or selected["updatedAt"] != updated_at:
                raise DesktopError("计划已变化，请查看最新状态后再操作。")
            allowed = ("suggested", "deferred") if action == "adopt" else ("suggested", "adopted")
            if selected["status"] not in allowed:
                raise DesktopError("当前计划状态不支持此操作。")
            try:
                if action == "adopt":
                    _, before = self._adopt_preparation(save, node_id, updated_at)
                    self._pending_life_preparation.setdefault(save, before)
                else:
                    deferred = self._milestone_store.defer(save, node_id, work_store=self._work_store,
                        game_date=game_date, expected_updated_at=updated_at)
                    if deferred.get("goalId") and self._autonomy.state(save).goal_scope == deferred["goalId"]:
                        self._autonomy.set_goal_scope(save, None)
            except MilestoneError as ex:
                raise DesktopError(str(ex)) from ex
            projection = self._build_milestones_state(save, "")
            await self._send_reply(ws, projection)
            await self._push_work_state(ws, save)
            if action == "adopt" and binding == self.binding():
                self._schedule_life_drain(ws)
            return projection.payload
