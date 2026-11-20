"""Local control API for the Windows desktop companion."""

from __future__ import annotations

import argparse
import asyncio
import hmac
import json
import logging
import os
import re
import shutil
import socket
import subprocess
import uuid
from collections import deque
from pathlib import Path
from typing import Any

import uvicorn
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse, StreamingResponse
from starlette.routing import Route

from .agent_backends import DshBackend, McodeBackend
from .chat_bridge import ChatBridge, Envelope
from .desktop_partner import DesktopPartner
from .external_connection import AcpError, read_binding
from .owner_process import OwnerProcessGuard
from .pi_backend import PiBackend
from .protocol import LifeMemoryEditPayload, LifeProfileSetPayload, ProtocolError
from .release_package import verify_release
from .websocket_client import WebSocketError

log = logging.getLogger(__name__)

CLIENTS = {"pi": "pi", "dsh": "dsh", "codex": "codex.exe", "mcode": "mcode"}
CLIENT_NAMES = {"pi": "Pi", "dsh": "DeepSeek (dsh)", "codex": "Codex", "mcode": "MiniMax Code"}


class DesktopError(RuntimeError):
    pass


class DesktopBridge(ChatBridge):
    _local_pi_sessions_only = True

    def __init__(self, run_dir: Path, model: str, client: str, *, backend: str = "pi"):
        super().__init__(run_dir=str(run_dir), backend=backend, model=model,
                         effort="low" if backend in {"dsh", "codex"} else "default",
                         enable_plan_worker=False, **({f"{backend}_cmd": client} if client else {}))
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
        self.settling: str | None = None
        self.partner = DesktopPartner(run_dir)

    def publish(self, kind: str, payload: dict) -> None:
        self.sequence += 1
        self.events.append((self.sequence, {"type": kind, "payload": payload}))

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
            state["clientName"] = CLIENT_NAMES[self.backend_name]
        elif not ready and state["status"] == "connected":
            state["status"] = "waiting"
        return {**state, "ready": ready, "ownedAdapter": self.partner.active()}

    def require_partner(self) -> None:
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
            "thinking": bool(self._busy_lock.locked() or self._active_task),
            "activity": self.native_state.get("activity", actor.get("activity")),
            "activeCommandId": self.native_state.get("activeCommandId", actor.get("activeCommandId")),
            "progress": self.native_state.get("progress"),
            "work": {key: work.get(key) for key in (
                "currentGoalId", "activeGoals", "recentTodos", "recentExecutions", "activity",
                "waitingConditions", "pauseReason",
            )},
            "decisions": [d for d in work.get("playerDecisions", []) or [] if d.get("status") == "pending"],
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
        shell = find_client("mcode", client)
        result = await self.partner.launch(cwd=cwd, data_dir=data_dir, command=shell,
                                           session=session if action == "attach" else "", name=name)
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


def create_app(bridge: DesktopBridge, token: str) -> Starlette:
    async def api(request: Request):
        if not hmac.compare_digest(request.headers.get("authorization", "").encode(), ("Bearer " + token).encode()):
            return JSONResponse({"error": "访问令牌无效。"}, status_code=401)
        path = request.url.path
        if request.method == "GET":
            if path == "/v1/status":
                return JSONResponse(bridge.status())
            if path != "/v1/events":
                entry = bridge.requests.get(request.path_params["request_id"])
                return JSONResponse(entry[1]) if entry else JSONResponse({"error": "请求不存在。"}, status_code=404)

            async def events():
                try:
                    cursor = int(request.headers.get("last-event-id", "0"))
                except ValueError:
                    cursor = 0
                previous = None
                while True:
                    if (current := bridge.status()) != previous:
                        previous = current
                        yield "data: " + json.dumps({"type": "status", "payload": current}, ensure_ascii=False) + "\n\n"
                    for index, event in tuple(bridge.events):
                        if index > cursor:
                            cursor = index
                            yield f"id: {index}\ndata: " + json.dumps(event, ensure_ascii=False) + "\n\n"
                    yield ": heartbeat\n\n"
                    await asyncio.sleep(0.6)

            return StreamingResponse(events(), media_type="text/event-stream", headers={"Cache-Control": "no-store"})

        try:
            data = bytearray()
            async for chunk in request.stream():
                data.extend(chunk)
                if len(data) > 8192:
                    return JSONResponse({"error": "请求太长。"}, status_code=413)
            body = json.loads(data)
            if not isinstance(body, dict):
                raise ValueError()
        except ValueError:
            return JSONResponse({"error": "请求格式无效。"}, status_code=400)
        try:
            if path == "/v1/partner":
                return JSONResponse(await bridge.external_partner(body))
            binding = bridge.binding()
            if body.get("gameSessionId") != binding[1]:
                raise DesktopError("请等待桌面状态刷新后再提交。")
            if path == "/v1/control":
                action = body.get("action")
                if action not in ("pause", "resume", "cancel", "takeover", "handoff"):
                    return JSONResponse({"error": "未知控制操作。"}, status_code=400)
                goal = body.get("goalId")
                if goal is not None and (action != "cancel" or not isinstance(goal, str) or not 1 <= len(goal) <= 120):
                    return JSONResponse({"error": "目标或控制操作无效。"}, status_code=400)
                await bridge.control(binding, action, **({"goalId": goal} if goal else {}))
                return JSONResponse({"confirmed": True})
            if path == "/v1/history":
                before = body.get("before")
                if before is not None and (type(before) is not int or not 1 <= before <= 2_147_483_647):
                    return JSONResponse({"error": "历史位置无效。"}, status_code=400)
                reply = await bridge.native_control(binding, "history", before=before)
                return JSONResponse(reply["history"])
            if path == "/v1/profile":
                if "patch" not in body:
                    return JSONResponse(bridge.preferences(binding[0]))
                patch, revision = body["patch"], body.get("expectedRevision")
                fields = {"companionName": str, "playStyle": str, "personality": str, "careFrequency": str,
                          "bedtime": int, "onboarded": bool, "skipped": bool}
                if (type(revision) is not int or revision < 0 or not isinstance(patch, dict) or not patch
                        or any(type(value) is not fields.get(key) for key, value in patch.items())):
                    return JSONResponse({"error": "伙伴设置格式无效。"}, status_code=400)
                return JSONResponse(await bridge.save_profile(binding, revision, patch))
            if path == "/v1/memory":
                if "op" not in body:
                    return JSONResponse(bridge._memory_store.list(binding[0]))
                op, entry, kind, text = (body.get(key) for key in ("op", "id", "kind", "text"))
                revision = body.get("expectedRevision")
                if (type(revision) is not int or revision < 0 or op not in ("add", "correct", "delete")
                        or op == "add" and kind not in ("preference", "agreement")
                        or op != "add" and (not isinstance(entry, str) or not 1 <= len(entry) <= 120)
                        or op != "delete" and (not isinstance(text, str) or not 1 <= len(text.strip()) <= 200)):
                    return JSONResponse({"error": "记忆内容或操作无效。"}, status_code=400)
                edit = {"op": op, "expectedRevision": revision}
                if op == "add":
                    edit["kind"] = kind
                else:
                    edit["id"] = entry
                if op != "delete":
                    edit["text"] = text.strip()
                return JSONResponse(await bridge.edit_memory(binding, edit))
            if path == "/v1/initiative":
                mode, preference = body.get("mode"), body.get("preference")
                if mode not in ("command", "free") or preference not in ("autonomous", "clear", "forage", "wood", "wait"):
                    return JSONResponse({"error": "主动帮忙偏好无效。"}, status_code=400)
                return JSONResponse(await bridge.save_initiative(binding, mode, preference))
            request_id = body.get("requestId")
            text = body.get("text")
            mode = body.get("mode", "send")
            decision = body.get("decisionId")
            if (not isinstance(request_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", request_id)
                    or not isinstance(text, str) or not 1 <= len(text.strip()) <= 500
                    or mode not in ("send", "interrupt")
                    or decision is not None and (not isinstance(decision, str) or not 1 <= len(decision) <= 120 or mode != "send")):
                return JSONResponse({"error": "指令或请求编号无效。"}, status_code=400)
            signature = (text.strip(), mode, decision)
            if current := bridge.requests.get(request_id):
                if signature != current[0]:
                    raise DesktopError("此请求编号已用于另一条指令。")
                return JSONResponse(current[1])
            if decision and not bridge.has_pending_decision(decision):
                raise DesktopError("该决定已结束，请刷新后重试。")
            if len(bridge.turns) >= 8:
                raise DesktopError("待处理指令已满，请稍后再试。")
            if len(bridge.requests) >= 2048:
                raise DesktopError("本次连接的指令记录已满，请在保存后重新连接。")
            if not bridge._control_utterance(text.strip()) or decision:
                bridge.require_partner()
            result = {"requestId": request_id, "status": "accepted"}
            bridge.requests[request_id] = (signature, result)
            bridge.publish("submitted", {**result, "text": text.strip()})
            task = asyncio.create_task(bridge.command(binding, request_id, text.strip(), mode == "interrupt", decision))
            bridge.turns.add(task)
            task.add_done_callback(bridge.turns.discard)
            return JSONResponse(result, status_code=202)
        except (DesktopError, AcpError) as ex:
            return JSONResponse({"error": str(ex)}, status_code=409)
        except ProtocolError:
            return JSONResponse({"error": "请求包含无效的值。"}, status_code=400)
        except (OSError, WebSocketError, TimeoutError):
            return JSONResponse({"error": "游戏连接中断，操作结果尚未确认。"}, status_code=503)

    return Starlette(routes=[
        Route("/v1/status", api), Route("/v1/events", api),
        Route("/v1/commands/{request_id}", api),
        Route("/v1/commands", api, methods=["POST"]), Route("/v1/control", api, methods=["POST"]),
        Route("/v1/history", api, methods=["POST"]), Route("/v1/profile", api, methods=["POST"]),
        Route("/v1/memory", api, methods=["POST"]),
        Route("/v1/initiative", api, methods=["POST"]),
        Route("/v1/partner", api, methods=["POST"]),
    ])


def find_client(backend: str, explicit: str) -> str:
    name = CLIENT_NAMES[backend]
    if explicit:
        if Path(explicit).is_file():
            suffix = Path(explicit).suffix.lower()
            if suffix not in ({".exe"} if backend == "codex" else {".exe", ".cmd"}):
                raise DesktopError(f"请选择 {name} 的客户端程序。")
            return str(Path(explicit).resolve())
        raise DesktopError(f"选择的 {name} 客户端文件不存在。")
    executable = CLIENTS[backend]
    if resolved := shutil.which(executable):
        return resolved
    paths = [Path(value) / "fnm" for key in ("LOCALAPPDATA", "APPDATA") if (value := os.getenv(key))]
    if fnm := os.getenv("FNM_DIR"):
        paths.insert(0, Path(fnm))
    shim = executable if executable.endswith(".exe") else executable + ".cmd"
    candidates = [path / "aliases" / "default" / shim for path in paths]
    if roaming := os.getenv("APPDATA"):
        candidates.append(Path(roaming) / "npm" / shim)
    if local := os.getenv("LOCALAPPDATA"):
        local = Path(local)
        if backend == "codex":
            candidates.extend(sorted((local / "OpenAI/Codex/bin").glob("*/codex.exe"), reverse=True))
        else:
            candidates.extend(sorted((local / "Packages").glob(f"OpenAI.Codex_*/LocalCache/Roaming/npm/{shim}")))
        if backend == "dsh":
            candidates.append(local / "Programs/DeepSeek Harness/resources/runtime/cli/bin/dsh.cmd")
    for path in candidates:
        if path.is_file():
            return str(path)
    raise DesktopError(f"未找到 {name}。请安装并登录客户端，或在桌面选择客户端路径。")


def check_client(run_dir: Path, backend: str, explicit: str) -> str:
    client = find_client(backend, explicit)
    try:
        if backend == "pi":
            executable = PiBackend(run_dir, command=client)._executable()
        elif backend == "dsh":
            executable = DshBackend(run_dir, command=client)._command()
        elif backend == "mcode":
            executable = McodeBackend(run_dir, command=client)._launch_args(client, cancel_hook=False)
        else:
            executable = [client]
        env = DshBackend._launch_env(executable, dict(os.environ)) if backend == "dsh" else None
        subprocess.run([*executable, "--version"], check=True, timeout=12, capture_output=True, env=env,
                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as ex:
        raise DesktopError(f"{CLIENT_NAMES[backend]} 启动检查失败，请检查客户端和运行环境。") from ex
    return client


async def serve(bridge: DesktopBridge, token: str, stop: asyncio.Event):
    with socket.create_server(("127.0.0.1", 0)) as listener:
        port = listener.getsockname()[1]
        config = uvicorn.Config(create_app(bridge, token), access_log=False, ws="none", timeout_graceful_shutdown=1)
        server = uvicorn.Server(config)
        runtime_task = asyncio.create_task(bridge.run(stop))
        web_task = asyncio.create_task(server.serve(sockets=[listener]))
        try:
            while not server.started and not web_task.done():
                await asyncio.sleep(0.03)
            if not server.started:
                raise DesktopError("本地控制服务未启动。")
            print(json.dumps({"port": port}), flush=True)
            done, _ = await asyncio.wait({runtime_task, web_task}, return_when=asyncio.FIRST_COMPLETED)
            for task in done:
                task.result()
        finally:
            stop.set()
            server.should_exit = True
            runtime_task.cancel()
            await asyncio.gather(runtime_task, web_task, return_exceptions=True)
            await bridge.partner.stop()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--run-dir", required=True, type=Path)
    parser.add_argument("--owner-pid", required=True, type=int)
    parser.add_argument("--model", default="")
    parser.add_argument("--backend", choices=CLIENTS, default="pi")
    parser.add_argument("--client", default="")
    parser.add_argument("--pi-cmd", default="", help=argparse.SUPPRESS)
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO)
    token = os.environ.pop("STARDEW_DESKTOP_TOKEN", "")
    if len(token) < 32:
        parser.error("Desktop launcher token missing")
    guard = OwnerProcessGuard(args.owner_pid)
    try:
        verify_release(args.run_dir)
        client = ("" if os.getenv("STARDEW_CONNECTION_SOURCE") == "external"
                  else check_client(args.run_dir, args.backend, args.client or args.pi_cmd))
        bridge = DesktopBridge(args.run_dir, args.model, client, backend=args.backend)
        asyncio.run(guard.run(lambda stop: serve(bridge, token, stop)))
    except (ValueError, OSError, DesktopError) as ex:
        print(json.dumps({"error": str(ex)}, ensure_ascii=True), flush=True)
        raise SystemExit(1) from ex
    finally:
        guard.close()


if __name__ == "__main__":
    main()
