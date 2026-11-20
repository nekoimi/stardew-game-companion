import asyncio
import json

import httpx

from stardew_ai_runtime.desktop_service import DesktopBridge, DesktopError, create_app


class DesktopHarness(DesktopBridge):
    def __init__(self, root):
        super().__init__(root, "", "pi")
        self.save = "Farm_1"
        self.game_session = self._player_session_id = "session-a"
        self._chat_ws = object()
        self._latest_snapshot_payload = {"companion": {"activity": "idle"}}
        self.calls = []
        self.disconnect_on_status = False
        self.receipt_reads = 0

    async def native_control(self, binding, action, **kwargs):
        self.calls.append((action, kwargs))
        if action == "interrupt":
            return {"interruptedCommandId": "old-action"}
        if action == "status":
            if self.disconnect_on_status:
                raise DesktopError("旧动作结果未确认")
            self.receipt_reads += 1
            return {"active": self.receipt_reads == 1,
                    "result": None if self.receipt_reads == 1 else {"commandId": "old-action", "terminalState": "cancelled"}}
        return {}

    async def handle_chat_cancel(self, ws, request_id, reason, save_id, **scope):
        self.calls.append(("cancel-turn", reason))
        if scope:
            return await super().handle_chat_cancel(ws, request_id, reason, save_id, **scope)

    async def _handle_life_message(self, ws, kind, payload, save_id):
        self.calls.append(("agent-input", payload))


class ReplySocket:
    def __init__(self):
        self.sent = []

    async def send_text(self, text):
        self.sent.append(json.loads(text))


def test_desktop_api_authenticates_binds_session_and_admits_one_request(tmp_path):
    bridge = DesktopHarness(tmp_path)
    token = "x" * 32
    body = {"gameSessionId": "session-a", "requestId": "new-command", "text": "把门口的作物浇完"}
    async def request_twice():
        transport = httpx.ASGITransport(app=create_app(bridge, token))
        async with httpx.AsyncClient(transport=transport, base_url="http://127.0.0.1") as client:
            assert (await client.post("/v1/commands", json=body)).status_code == 401
            headers = {"Authorization": f"Bearer {token}"}
            assert (await client.post("/v1/commands", headers=headers, json={**body, "gameSessionId": "old"})).status_code == 409
            assert (await client.post("/v1/commands", headers=headers, json=body)).status_code == 202
            assert (await client.post("/v1/commands", headers=headers, json=body)).status_code == 200
            assert (await client.post("/v1/commands", headers=headers, json={**body, "text": "收获"})).status_code == 409
            assert (await client.get("/v1/commands/new-command", headers=headers)).json()["requestId"] == "new-command"
            await asyncio.gather(*bridge.turns)
    asyncio.run(request_twice())
    assert [call[0] for call in bridge.calls] == ["record", "agent-input"]
    assert bridge.calls[-1][1]["source"] == "desktop"


def test_interrupt_waits_for_old_native_receipt_before_new_agent_input(tmp_path):
    bridge = DesktopHarness(tmp_path)
    asyncio.run(bridge.command(bridge.binding(), "replace", "现在去浇水", True, None))
    assert [call[0] for call in bridge.calls] == [
        "interrupt", "cancel-turn", "status", "status", "record", "unhold", "agent-input",
    ]
    assert bridge.calls[3][1]["commandId"] == "old-action"
    assert bridge.calls[-1][1]["text"] == "现在去浇水"


def test_interrupt_with_unknown_native_result_keeps_dispatch_gate_closed(tmp_path):
    bridge = DesktopHarness(tmp_path)
    bridge.disconnect_on_status = True
    failed = []

    async def send_reply(ws, reply):
        failed.append(reply.payload)

    bridge._send_reply = send_reply
    asyncio.run(bridge.command(bridge.binding(), "replace", "现在去浇水", True, None))
    assert [call[0] for call in bridge.calls] == ["interrupt", "cancel-turn", "status"]
    assert bridge.settling == "old-action"
    assert failed[0]["status"] == "failed"
    assert failed[0]["error"] == "旧动作结果未确认"


def test_desktop_withdraws_background_goal_without_interrupting_current_goal(tmp_path):
    bridge = DesktopHarness(tmp_path)
    bridge._chat_ws = ReplySocket()
    current = bridge._work_store.add_goal(bridge.save, "照料动物", source="user")
    background = bridge._work_store.add_goal(bridge.save, "清理树林", source="agent")
    bridge._autonomy.set_goal_scope(bridge.save, current.id)

    asyncio.run(bridge.control(bridge.binding(), "cancel", goalId=background.id))

    assert bridge.calls == []
    goals = {g["id"]: g["status"] for g in bridge._work_store.list_goals(bridge.save)}
    assert goals == {current.id: "active", background.id: "cancelled"}
    assert bridge.status()["work"]["currentGoalId"] == current.id


def test_desktop_withdraws_current_goal_and_waits_for_native_terminal(tmp_path):
    bridge = DesktopHarness(tmp_path)
    bridge._chat_ws = ReplySocket()
    goal = bridge._work_store.add_goal(bridge.save, "照料动物", source="user")
    bridge._autonomy.set_goal_scope(bridge.save, goal.id)

    asyncio.run(bridge.control(bridge.binding(), "cancel", goalId=goal.id))

    assert [name for name, _ in bridge.calls] == ["interrupt", "cancel-turn", "status", "status", "unhold"]
    assert bridge.calls[3][1]["commandId"] == "old-action"
    assert bridge._work_store.list_goals(bridge.save)[0]["status"] == "cancelled"


def test_desktop_reply_is_bound_to_selected_decision(tmp_path):
    bridge = DesktopHarness(tmp_path)
    goal = bridge._work_store.add_goal(bridge.save, "照料动物", source="user")
    first = bridge._work_store.request_player_decision(bridge.save, goal.id, "饲料够用吗？")
    second = bridge._work_store.request_player_decision(bridge.save, goal.id, "先照料哪种动物？")
    assert [d["id"] for d in bridge.status()["decisions"]] == [first["id"], second["id"]]

    asyncio.run(bridge.command(bridge.binding(), "answer", "先照料鸡", False, second["id"]))

    assert bridge.calls[0][1]["decisionId"] == second["id"]
    assert bridge.calls[1][1]["replyToNoticeId"] == second["id"]
    assert bridge.calls[1][1]["resolveNotice"] is True


def test_desktop_profile_uses_existing_revision_and_game_notification(tmp_path):
    bridge = DesktopHarness(tmp_path)
    ws = bridge._chat_ws = ReplySocket()
    bridge._profile_store.set(bridge.save, {"companionName": "星星", "personality": "calm"}, 0)
    headers = {"Authorization": "Bearer token"}

    async def verify():
        transport = httpx.ASGITransport(app=create_app(bridge, "token"))
        async with httpx.AsyncClient(transport=transport, base_url="http://127.0.0.1", headers=headers) as client:
            patch = {"gameSessionId": "session-a", "expectedRevision": 0, "patch": {"bedtime": 2230}}
            assert (await client.post("/v1/profile", json={**patch, "gameSessionId": "old"})).status_code == 409
            assert ws.sent == []
            assert (await client.post("/v1/profile", json=patch)).status_code == 409
            assert ws.sent[-1]["payload"]["reason"] == "STALE_REVISION"
            response = await client.post("/v1/profile", json={**patch, "expectedRevision": 1})
            assert response.status_code == 200
            assert response.json()["profile"]["companionName"] == "星星"
            assert response.json()["profile"]["personality"] == "calm"
            native = ws.sent[-1]
            assert native["messageType"] == "life.profile.state"
            assert native["payload"]["profile"]["bedtime"] == 2230
            assert native["payload"]["profileRevision"] == 2

    asyncio.run(verify())


def test_desktop_initiative_settings_preserve_pause_and_goal(tmp_path):
    bridge = DesktopHarness(tmp_path)
    bridge._chat_ws = ReplySocket()
    bridge._autonomy.set_goal_scope(bridge.save, "goal-scope")
    bridge._autonomy.set_paused(bridge.save, True)

    updated = asyncio.run(bridge.save_initiative(bridge.binding(), "free", "wait"))

    assert updated["initiativeMode"] == "free"
    assert updated["idlePreference"] == "wait"
    state = bridge._autonomy.state(bridge.save)
    assert state.paused and state.goal_scope == "goal-scope"


def test_history_reply_after_save_switch_is_not_returned_to_desktop(tmp_path):
    bridge = DesktopBridge(tmp_path, "", "pi")
    bridge.save = "Farm_1"
    bridge.game_session = bridge._player_session_id = "session-a"
    bridge._latest_snapshot_payload = {"companion": {"activity": "idle"}}

    class SwitchingSocket:
        async def send_text(self, text):
            request = json.loads(text)
            bridge.desktop_message({**request, "messageType": "desktop.control.result", "payload": {
                "requestId": request["payload"]["requestId"], "history": {"entries": [{"text": "旧存档记录"}]},
            }}, "Farm_1")
            bridge.save = "Farm_2"
            bridge.game_session = bridge._player_session_id = "session-b"

    bridge._chat_ws = SwitchingSocket()
    bridge.native_state = {"paused": True}

    async def verify():
        transport = httpx.ASGITransport(app=create_app(bridge, "token"))
        async with httpx.AsyncClient(transport=transport, base_url="http://127.0.0.1",
                                     headers={"Authorization": "Bearer token"}) as client:
            response = await client.post("/v1/history", json={"gameSessionId": "session-a"})
            assert response.status_code == 409
            assert "旧存档记录" not in response.text
            assert bridge.native_state == {"paused": True}

    asyncio.run(verify())


def test_desktop_memory_edits_share_revision_and_stay_in_the_loaded_save(tmp_path):
    bridge = DesktopHarness(tmp_path)
    ws = bridge._chat_ws = ReplySocket()
    bridge._memory_store.add("OtherFarm", kind="agreement", text="保留原来的约定", source="player",
                             game_date="unknown", expected_revision=0)

    async def verify():
        transport = httpx.ASGITransport(app=create_app(bridge, "token"))
        async with httpx.AsyncClient(transport=transport, base_url="http://127.0.0.1",
                                     headers={"Authorization": "Bearer token"}) as client:
            scope = {"gameSessionId": "session-a"}

            async def edit(**changes):
                return await client.post("/v1/memory", json={**scope, **changes})

            assert (await edit()).json() == {"memoryRevision": 0, "entries": []}
            assert (await edit(gameSessionId="old", op="add", expectedRevision=0,
                               kind="agreement", text="旧存档会话")).status_code == 409
            response = await edit(op="add", expectedRevision=0, kind="agreement", text="保留幼树")
            assert response.status_code == 200
            memory = response.json()
            item = memory["entries"][0]
            assert item["source"] == "player"
            assert ws.sent[-1]["messageType"] == "life.memory.state"
            assert ws.sent[-1]["payload"]["memoryRevision"] == memory["memoryRevision"]

            stale = await edit(op="correct", expectedRevision=0, id=item["id"], text="旧版本")
            assert stale.status_code == 409
            response = await edit(op="correct", expectedRevision=memory["memoryRevision"], id=item["id"], text="门口保留幼树")
            assert response.status_code == 200
            assert response.json()["entries"][0]["text"] == "门口保留幼树"
            response = await edit(op="delete", expectedRevision=response.json()["memoryRevision"], id=item["id"])
            assert response.status_code == 200 and response.json()["entries"] == []

    asyncio.run(verify())
    assert bridge._memory_store.list("OtherFarm")["entries"][0]["text"] == "保留原来的约定"
