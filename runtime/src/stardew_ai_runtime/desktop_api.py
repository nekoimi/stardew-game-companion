"""Authenticated loopback HTTP and event streaming for desktop controls."""

from __future__ import annotations

import asyncio
import hmac
import json
import re

from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse, StreamingResponse
from starlette.routing import Route

from .desktop_runtime import DesktopBridge, DesktopError
from .protocol import ProtocolError
from .websocket_client import WebSocketError


def create_app(bridge: DesktopBridge, token: str) -> Starlette:
    async def api(request: Request):
        if not hmac.compare_digest(request.headers.get("authorization", "").encode(), ("Bearer " + token).encode()):
            return JSONResponse({"error": "访问令牌无效。"}, status_code=401)
        path = request.url.path
        if request.method == "GET":
            if path == "/v1/status":
                return JSONResponse(bridge.status())
            if path != "/v1/events":
                entry = bridge.command_result(request.path_params["request_id"])
                return JSONResponse(entry) if entry else JSONResponse({"error": "请求不存在。"}, status_code=404)

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
                    for index, event in bridge.events_after(cursor):
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
            if path == "/v1/usage":
                return JSONResponse(bridge.usage(body.get("gameSessionId")), headers={"Cache-Control": "no-store"})
            binding = bridge.binding()
            if body.get("gameSessionId") != binding[1]:
                raise DesktopError("请等待桌面状态刷新后再提交。")
            if path == "/v1/control":
                action = body.get("action")
                if action not in ("pause", "resume", "cancel", "takeover", "handoff", "recover"):
                    return JSONResponse({"error": "未知控制操作。"}, status_code=400)
                goal = body.get("goalId")
                if goal is not None and (action != "cancel" or not isinstance(goal, str) or not 1 <= len(goal) <= 120):
                    return JSONResponse({"error": "目标或控制操作无效。"}, status_code=400)
                await bridge.control(binding, action, **({"goalId": goal} if goal else {}))
                return JSONResponse({"confirmed": True})
            if path in ("/v1/history", "/v1/messages"):
                if path == "/v1/messages" and "readId" in body:
                    entry = body["readId"]
                    if not isinstance(entry, str) or not 1 <= len(entry) <= 200 or not entry.startswith("care:"):
                        return JSONResponse({"error": "消息编号无效。"}, status_code=400)
                    await bridge.native_control(binding, "read-message", entryId=entry)
                    return JSONResponse({"confirmed": True})
                before = body.get("before")
                if before is not None and (type(before) is not int or not 1 <= before <= 2_147_483_647):
                    return JSONResponse({"error": "历史位置无效。"}, status_code=400)
                reply = await bridge.native_control(binding, "inbox" if path == "/v1/messages" else "history", before=before)
                return JSONResponse(reply["history"])
            if path == "/v1/notices":
                action, notice_id = body.get("action"), body.get("noticeId")
                if action not in ("dismiss", "restore") or not isinstance(notice_id, str) or not 1 <= len(notice_id) <= 120:
                    return JSONResponse({"error": "待确认事项操作无效。"}, status_code=400)
                await bridge.change_decision_visibility(binding, action, notice_id)
                return JSONResponse({"confirmed": True})
            if path == "/v1/milestones":
                action, node, updated = body.get("action"), body.get("nodeId"), body.get("updatedAt")
                if (action not in ("adopt", "defer") or not isinstance(node, str) or not 1 <= len(node) <= 120
                        or type(updated) not in (int, float) or not 0 <= updated <= 1e12):
                    return JSONResponse({"error": "计划操作无效。"}, status_code=400)
                return JSONResponse(await bridge.change_milestone(binding, action, node, updated))
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
                    return JSONResponse(bridge.memory(binding))
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
            result, accepted = bridge.submit_command(binding, request_id, text.strip(), mode, decision)
            return JSONResponse(result, status_code=202 if accepted else 200)
        except DesktopError as ex:
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
        Route("/v1/messages", api, methods=["POST"]), Route("/v1/notices", api, methods=["POST"]),
        Route("/v1/milestones", api, methods=["POST"]),
        Route("/v1/usage", api, methods=["POST"]),
    ])
