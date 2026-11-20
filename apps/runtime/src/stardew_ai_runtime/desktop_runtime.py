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

from .adapters.providers.mcode_external import AcpError, read_binding
from .application.player_service import PlayerOperationError as DesktopError
from .application.player_service import PlayerService
from .chat_bridge import ChatBridge
from .desktop_partner import DesktopPartner
from .protocol import Envelope
from .provider_registry import PROVIDERS, ClientError

log = logging.getLogger(__name__)


class DesktopBridge:

    def __init__(self, run_dir: Path, model: str, client: str, *, backend: str = "pi", effort: str = ""):
        provider = PROVIDERS[backend]
        if effort and effort not in provider.efforts:
            raise DesktopError(f"{provider.name} 不支持所选推理档位。")
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
        self.runtime = ChatBridge(run_dir=run_dir, backend=backend, model=model,
                                  effort=effort or provider.default_effort, backend_command=client,
                                  enable_plan_worker=False, events=self, explicit_handoff=True,
                                  local_pi_sessions_only=True)
        self.player = PlayerService(self.runtime)

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
        return self.player.usage(save)

    def memory(self, binding) -> dict:
        return self.player.memory(binding[0])

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
        if not self.player.control_utterance(text) or decision:
            self.require_partner()
        result = {"requestId": request_id, "status": "accepted"}
        self.requests[request_id] = (signature, result)
        self.publish("submitted", {**result, "text": text})
        task = asyncio.create_task(self.command(binding, request_id, text, mode == "interrupt", decision))
        self.turns.add(task)
        task.add_done_callback(self.turns.discard)
        return result, True

    async def run(self, stop_event: asyncio.Event | None = None) -> None:
        await self.runtime.run(stop_event)

    def game_loaded(self, session_id: str | None) -> None:
        if session_id != self.game_session:
            self.game_session = session_id
            self.requests.clear()
            self.events.clear()
            self.settling = None

    def game_connected(self, save_id: str | None) -> None:
        self.save = save_id
        self.native_state = {}
        self._awaiting_native_control = True

    async def game_disconnected(self) -> None:
        self.save = None
        for task in tuple(self.turns):
            task.cancel()
        await asyncio.gather(*self.turns, return_exceptions=True)
        for _, result in self.requests.values():
            if result["status"] in {"accepted", "queued", "processing"}:
                result.update(status="unknown", error="连接中断，指令结果尚未确认。")
                self.publish("life.chat.reply", result.copy())

    def publish_reply(self, reply: Envelope) -> None:
        if reply.message_type in {"chat.reply", "life.chat.reply", "life.care"} and reply.payload.get("saveId") == self.save:
            self.publish(reply.message_type, reply.payload)
            if entry := self.requests.get(reply.payload.get("requestId", "")):
                entry[1].update(reply.payload)

    def control_pending(self) -> bool:
        return self._desktop_control_pending

    def native_event(self, envelope: dict, active_save_id: str | None) -> None:
        if envelope.get("saveId") != active_save_id or envelope.get("gameSessionId") != self.player.game_session:
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
        ws = self.player.game_connection
        session = self.player.game_session
        if not self.save or not session or session != self.game_session or ws is None:
            raise DesktopError("请先启动游戏并进入存档，等待伙伴连接。")
        return self.save, session, ws

    def permits_dispatch(self) -> bool:
        return not (self._awaiting_native_control or self.native_state.get("recoveryPending")) and self._partner_available()

    def _partner_available(self) -> bool:
        if not self.runtime.partner_connected():
            return False
        if self.runtime.connection_source != "external":
            return True
        binding = read_binding(self.runtime.run_dir)
        return bool(binding and self.player.current_partner_binding(binding))

    def connection_status(self) -> dict:
        state = self.player.partner_status()
        ready = self.permits_dispatch()
        if self.runtime.connection_source == "builtin":
            state["clientName"] = PROVIDERS[self.runtime.backend_name].name
        elif not self._partner_available() and state["status"] == "connected":
            state["status"] = "waiting"
        return {**state, "ready": ready, "ownedAdapter": self.partner.active()}

    def require_partner(self) -> None:
        if self._awaiting_native_control or self.native_state.get("recoveryPending"):
            raise DesktopError("请先等待游戏状态同步，或核对旧动作后继续。")
        if not self.permits_dispatch():
            raise DesktopError("原伙伴尚未连接，请在设置中恢复原会话。")

    def status(self) -> dict:
        connection = self.connection_status()
        try:
            save, session, _ = self.binding()
        except DesktopError:
            return {"ready": False, "connection": connection}
        actor, work, milestones, thinking = self.player.work_status(save)
        return {
            "ready": True, "saveId": save, "gameSessionId": session, "connection": connection,
            "actorMode": self.runtime.actor_mode, "controlOwner": self.runtime.control_owner,
            "paused": bool(work.get("paused") or self.native_state.get("paused")),
            "interrupting": bool(self._desktop_control_pending or self.native_state.get("interrupting")),
            "recoveryPending": bool(self.native_state.get("recoveryPending")),
            "thinking": thinking,
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
            "milestones": milestones,
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
        await self.player.cancel_turn_for_replacement(ws, save)
        await self.settle(binding)
        self.player.native_cancel_confirmed(save)

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
                    self.player.pause_for_recovery(save)
                else:
                    raise DesktopError("当前没有待核对的断线动作。")
                reply = await self.native_control(binding, "recover")
                self.settling = None
                self.player.native_cancel_confirmed(save)
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
                        self.player.native_cancel_confirmed(save)
                        await self.native_control(binding, "unhold")
                targeted_cancel = action == "cancel" and bool(params.get("goalId"))
                if action == "cancel" and not targeted_cancel:
                    await hold_for_cancel()
                confirmed = await self.player.control(
                    ws, save, action, before_current_cancel=hold_for_cancel if targeted_cancel else None, **params)
                if held_native:
                    await self.settle(binding)
                    self.player.native_cancel_confirmed(save)
                    await self.native_control(binding, "unhold")
                if not confirmed and not held_native:
                    raise DesktopError("运行时尚未确认操作。")
                if action == "resume":
                    await self.native_control(binding, "resume")
            finally:
                self._desktop_control_pending = False

    def preferences(self, save: str) -> dict:
        return self.player.preferences(save)

    async def edit_memory(self, binding, body: dict) -> dict:
        async with self.operation:
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新读取记忆与约定。")
            save, _, ws = binding
            result = await self.player.edit_memory(ws, save, body)
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新读取记忆与约定。")
            return result

    async def save_profile(self, binding, revision: int, patch: dict) -> dict:
        async with self.operation:
            if self.runtime.connection_source == "external":
                raise DesktopError("原伙伴的人格与角色设置由原客户端管理。")
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新读取伙伴设置。")
            save, _, ws = binding
            result, delivered = await self.player.save_profile(ws, save, revision, patch)
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新读取伙伴设置。")
            if result.get("reason") == "STALE_REVISION":
                raise DesktopError("伙伴设置已变化，请重新读取后再保存。")
            if result.get("status") != "confirmed":
                raise DesktopError("伙伴设置未保存，请检查填写的内容。")
            if not delivered:
                raise DesktopError("设置已写入存档，游戏尚未确认接收。")
            return self.preferences(save)

    async def external_partner(self, body: dict) -> dict:
        if self.runtime.connection_source != "external":
            raise DesktopError("请先选择「接续原 MiniMax Code 会话」并重新启动。")
        action = body.get("action")
        if action == "disconnect":
            if not self.partner.active():
                raise DesktopError("此连接由原客户端启动，请在那里断开。")
            self.player.partner_disconnecting()
            await self.partner.stop()
            return {"disconnected": True}
        cwd, data_dir, client, session, name = (body.get(key, "") for key in ("cwd", "dataDir", "client", "sessionId", "name"))
        if (action not in ("list", "attach") or any(not isinstance(value, str) or "\0" in value
                for value in (cwd, data_dir, client, session, name)) or len(session) > 200 or len(name) > 80):
            raise DesktopError("请选择有效的原工作目录和会话。")
        if action == "attach":
            if not session or body.get("originalClientIdle") is not True:
                raise DesktopError("请选择原会话，并确认原聊天窗口保持闲置。")
            old = read_binding(self.runtime.run_dir)
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
            await self.player.partner_attached(check_now=not self.save)
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
            for action, params in self.player.initiative_changes(save, mode, preference):
                if not await self.player.control(ws, save, action, **params):
                    raise DesktopError("主动帮忙设置尚未确认，请重新读取。")
                if binding != self.binding():
                    raise DesktopError("游戏会话已变化，请重新读取伙伴设置。")
            return self.preferences(save)

    async def command(self, binding, request_id: str, text: str, interrupt: bool, decision: str | None):
        save, _, ws = binding
        try:
            action = self.player.control_utterance(text)
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
                        if self.status().get("paused") or self.runtime.actor_mode == "player" and self.runtime.control_owner != "agent":
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
            await self.player.submit_message(ws, save, request_id, text, decision=decision)
        except asyncio.CancelledError:
            raise
        except Exception as ex:
            if not isinstance(ex, DesktopError):
                log.exception("Desktop command failed")
            error = str(ex) if isinstance(ex, DesktopError) else "连接异常，指令结果未确认。"
            await self.player.report_failed_command(ws, save, request_id, error)

    def has_pending_decision(self, decision: str) -> bool:
        save, _, _ = self.binding()
        return self.player.has_pending_decision(save, decision)

    async def change_decision_visibility(self, binding, action: str, notice_id: str) -> None:
        async with self.operation:
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新操作。")
            save, _, ws = binding
            await self.player.change_decision_visibility(ws, save, action, notice_id)

    async def change_milestone(self, binding, action: str, node_id: str, updated_at: float) -> dict:
        async with self.operation:
            if binding != self.binding():
                raise DesktopError("游戏会话已变化，请重新操作。")
            save, _, ws = binding
            return await self.player.change_milestone(
                ws, save, action, node_id, updated_at, lambda: binding == self.binding())
