import asyncio
import json
from contextlib import asynccontextmanager
from datetime import UTC, datetime, timedelta

import httpx
import pytest

from stardew_ai_runtime.adapters.desktop_api.http import create_app
from stardew_ai_runtime.adapters.desktop_api.runtime import DesktopBridge, DesktopError
from stardew_ai_runtime.adapters.providers.mcode_external import write_binding
from stardew_ai_runtime.agent_backends import CodexBackend, DshBackend, McodeBackend
from stardew_ai_runtime.chat_bridge import ActiveChatTask
from stardew_ai_runtime.provider_registry import PROVIDERS, ProviderSpec, desktop_catalogue


class DesktopHarness(DesktopBridge):
    def __init__(self, root):
        super().__init__(root, "", "pi")
        self.save = "Farm_1"
        self.game_session = self.runtime._player_session_id = "session-a"
        self.runtime._chat_ws = object()
        self.runtime._latest_snapshot_payload = {"companion": {"activity": "idle"}}
        self._awaiting_native_control = False
        self.calls = []
        self.disconnect_on_status = False
        self.receipt_reads = 0
        self._original_cancel = self.runtime.handle_chat_cancel
        self.runtime.handle_chat_cancel = self.handle_chat_cancel
        self.runtime._handle_life_message = self._handle_life_message

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
            return await self._original_cancel(ws, request_id, reason, save_id, **scope)

    async def _handle_life_message(self, ws, kind, payload, save_id):
        self.calls.append(("agent-input", payload))


class ReplySocket:
    def __init__(self):
        self.sent = []

    async def send_text(self, text):
        self.sent.append(json.loads(text))


@pytest.mark.parametrize("repeated", [True, False])
def test_desktop_turn_marks_only_an_already_spoken_final_reply(tmp_path, repeated):
    bridge = DesktopHarness(tmp_path)
    bridge.runtime._chat_ws = socket = ReplySocket()
    speech = "今天可以先浇水。"
    final = speech if repeated else "水壶还需要补水。"

    @asynccontextmanager
    async def no_pause(*args):
        yield

    def provider(task, session, prompt, mode):
        path = tmp_path / "data" / "life-controls" / f"{task.request_id}--speech.json"
        path.parent.mkdir(exist_ok=True)
        path.write_text(json.dumps({"saveId": task.save_id, "action": "speak", "text": speech}), encoding="utf-8")
        return {"success": True, "response": final}

    bridge.runtime._thinking_pause = no_pause
    bridge.runtime._execute_life_turn = provider
    bridge.runtime._work_store.set_paused(bridge.save, True)  # Conversation needs no mock native action.
    asyncio.run(bridge.runtime._run_life_chat_turn(socket, {"request_id": "chat", "save_id": bridge.save, "text": "今天做什么？"}))

    events = [event["payload"] for _, event in bridge.events_after(0) if event["payload"].get("replyText")]
    assert [event["status"] for event in events] == ["public-message", "completed"]
    assert [event["replyText"] for event in events] == [speech, final]
    assert events[-1].get("replyAlreadyDelivered", False) is repeated
    assert any(message.get("payload") == events[-1] for message in socket.sent)


def test_desktop_recovery_waits_for_native_receipt_before_dispatching(tmp_path):
    class RecoverySocket(ReplySocket):
        async def send_text(self, text):
            await super().send_text(text)
            request = self.sent[-1]["payload"]
            assert request["action"] == "recover"
            assert bridge.runtime._work_store.state(save).paused and bridge.runtime._autonomy.state(save).paused
            project(recoveryPending=receipt is None, requestId=request["requestId"], result=receipt,
                    error="TERMINAL_UNKNOWN" if receipt is None else None)

    def project(**payload):
        bridge.native_event({"messageType": "desktop.control.result", "saveId": save,
                                "gameSessionId": session, "payload": {"paused": True, **payload}}, save)

    bridge = DesktopBridge(tmp_path, "", "pi")
    save, session = "Farm_1", "session-a"
    bridge.save = save
    bridge.game_session = bridge.runtime._player_session_id = session
    bridge.runtime._latest_snapshot_payload = {"companion": {"activity": "idle"}}
    bridge.runtime._chat_ws = socket = RecoverySocket()
    bridge.runtime.actor_mode, bridge.runtime.control_owner = "player", "human"
    receipt = None

    async def verify():
        nonlocal receipt
        transport = httpx.ASGITransport(app=create_app(bridge, "token"))
        async with httpx.AsyncClient(transport=transport, base_url="http://127.0.0.1",
                                     headers={"Authorization": "Bearer token"}) as client:
            async def control(action):
                return await client.post("/v1/control", json={"gameSessionId": session, "action": action})

            async def submit():
                return await client.post("/v1/commands", json={"gameSessionId": session,
                                                              "requestId": "fresh", "text": "浇水"})

            assert not bridge.permits_dispatch()  # No native projection after runtime startup.
            assert (await control("handoff")).status_code == 409
            assert (await submit()).status_code == 409
            project(recoveryPending=True)
            assert bridge.status()["recoveryPending"]
            assert not bridge.permits_dispatch()
            assert (await control("resume")).status_code == 409
            assert (await submit()).status_code == 409
            assert socket.sent == [] and bridge.turns == set()

            assert (await control("recover")).status_code == 409
            assert bridge.status()["recoveryPending"] and bridge.status()["paused"]
            assert bridge.events_after(0) == []
            assert (await control("handoff")).status_code == 409

            receipt = {"commandId": "old-action", "terminalState": "cancelled", "completedCount": 2}
            assert (await control("recover")).status_code == 200
            assert bridge.events_after(0)[0][1] == {"type": "result", "payload": receipt}
            assert bridge.status()["paused"] and bridge.status()["controlOwner"] == "human"
            assert bridge.permits_dispatch() and not bridge.status()["recoveryPending"]
            assert [item["payload"]["action"] for item in socket.sent] == ["recover", "recover"]

    asyncio.run(verify())


def test_desktop_usage_shares_receipts_and_only_projects_the_current_game_session(tmp_path):
    bridge = DesktopHarness(tmp_path)
    today = datetime.now(UTC)
    base = {"provider": "codex", "saveId": bridge.save, "model": "fixture", "timestamp": today.isoformat()}
    first = {**base, "requestId": "here", "usage": {"input_tokens": 100, "output_tokens": 10, "cache_read_tokens": 60,
                                                    "cost_usd": 0.25}}
    rows = [first, first, {**base, "requestId": "unknown", "usage": None},
            {**base, "requestId": "older", "timestamp": (today - timedelta(days=2)).isoformat(),
             "usage": {"input_tokens": 200, "output_tokens": 20, "cache_read_tokens": 120}},
            {**base, "requestId": "elsewhere", "saveId": "OtherFarm", "usage": {"input_tokens": 300, "output_tokens": 30,
                                                                          "cache_read_tokens": 0, "estimated_api_cost_usd": 0.5}}]
    bridge.runtime._commands_file.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")

    async def verify():
        transport = httpx.ASGITransport(app=create_app(bridge, "token"))
        async with httpx.AsyncClient(transport=transport, base_url="http://127.0.0.1") as client:
            body = {"gameSessionId": "session-a", "saveId": "OtherFarm"}
            assert (await client.post("/v1/usage", json=body)).status_code == 401
            headers = {"Authorization": "Bearer token"}
            summary = (await client.post("/v1/usage", json=body, headers=headers)).json()
            assert "440+未记录 token" in summary["usageTodayText"]
            assert "API 等价估算 $0.5000" in summary["usageTodayText"]
            assert "110+未记录 token" in summary["usageSaveTodayText"]
            assert "已提供费用 $0.2500" in summary["usageSaveTodayText"]
            assert "330+未记录 token" in summary["usageSaveTotalText"]

            bridge.save = None
            assert (await client.post("/v1/usage", json=body, headers=headers)).status_code == 409
            all_saves = (await client.post("/v1/usage", json={}, headers=headers)).json()
            assert all_saves["usageTodayText"] == summary["usageTodayText"]
            assert all_saves["usageSaveTodayText"] is None and all_saves["usageSaveTotalText"] is None

    asyncio.run(verify())
    assert bridge.calls == []


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

    bridge.runtime._send_reply = send_reply
    asyncio.run(bridge.command(bridge.binding(), "replace", "现在去浇水", True, None))
    assert [call[0] for call in bridge.calls] == ["interrupt", "cancel-turn", "status"]
    assert bridge.settling == "old-action"
    assert failed[0]["status"] == "failed"
    assert failed[0]["error"] == "旧动作结果未确认"


def test_desktop_withdraws_background_goal_without_interrupting_current_goal(tmp_path):
    bridge = DesktopHarness(tmp_path)
    bridge.runtime._chat_ws = ReplySocket()
    current = bridge.runtime._work_store.add_goal(bridge.save, "照料动物", source="user")
    background = bridge.runtime._work_store.add_goal(bridge.save, "清理树林", source="agent")
    bridge.runtime._autonomy.set_goal_scope(bridge.save, current.id)

    asyncio.run(bridge.control(bridge.binding(), "cancel", goalId=background.id))

    assert bridge.calls == []
    goals = {g["id"]: g["status"] for g in bridge.runtime._work_store.list_goals(bridge.save)}
    assert goals == {current.id: "active", background.id: "cancelled"}
    assert bridge.status()["work"]["currentGoalId"] == current.id


def test_desktop_withdraws_current_goal_and_waits_for_native_terminal(tmp_path):
    bridge = DesktopHarness(tmp_path)
    bridge.runtime._chat_ws = ReplySocket()
    goal = bridge.runtime._work_store.add_goal(bridge.save, "照料动物", source="user")
    bridge.runtime._autonomy.set_goal_scope(bridge.save, goal.id)

    asyncio.run(bridge.control(bridge.binding(), "cancel", goalId=goal.id))

    assert [name for name, _ in bridge.calls] == ["interrupt", "cancel-turn", "status", "status", "unhold"]
    assert bridge.calls[3][1]["commandId"] == "old-action"
    assert bridge.runtime._work_store.list_goals(bridge.save)[0]["status"] == "cancelled"


def test_desktop_reply_is_bound_to_selected_decision(tmp_path):
    bridge = DesktopHarness(tmp_path)
    goal = bridge.runtime._work_store.add_goal(bridge.save, "照料动物", source="user")
    first = bridge.runtime._work_store.request_player_decision(bridge.save, goal.id, "饲料够用吗？")
    second = bridge.runtime._work_store.request_player_decision(bridge.save, goal.id, "先照料哪种动物？")
    assert [d["id"] for d in bridge.status()["decisions"]] == [first["id"], second["id"]]

    asyncio.run(bridge.command(bridge.binding(), "answer", "先照料鸡", False, second["id"]))

    assert bridge.calls[0][1]["decisionId"] == second["id"]
    assert bridge.calls[1][1]["replyToNoticeId"] == second["id"]
    assert bridge.calls[1][1]["resolveNotice"] is True


def test_desktop_profile_uses_existing_revision_and_game_notification(tmp_path):
    bridge = DesktopHarness(tmp_path)
    ws = bridge.runtime._chat_ws = ReplySocket()
    bridge.runtime._profile_store.set(bridge.save, {"companionName": "星星", "personality": "calm"}, 0)
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
    bridge.runtime._chat_ws = ReplySocket()
    bridge.runtime._autonomy.set_goal_scope(bridge.save, "goal-scope")
    bridge.runtime._autonomy.set_paused(bridge.save, True)

    updated = asyncio.run(bridge.save_initiative(bridge.binding(), "free", "wait"))

    assert updated["initiativeMode"] == "free"
    assert updated["idlePreference"] == "wait"
    state = bridge.runtime._autonomy.state(bridge.save)
    assert state.paused and state.goal_scope == "goal-scope"


def test_history_reply_after_save_switch_is_not_returned_to_desktop(tmp_path):
    bridge = DesktopBridge(tmp_path, "", "pi")
    bridge.save = "Farm_1"
    bridge.game_session = bridge.runtime._player_session_id = "session-a"
    bridge.runtime._latest_snapshot_payload = {"companion": {"activity": "idle"}}

    class SwitchingSocket:
        async def send_text(self, text):
            request = json.loads(text)
            bridge.native_event({**request, "messageType": "desktop.control.result", "payload": {
                "requestId": request["payload"]["requestId"], "history": {"entries": [{"text": "旧存档记录"}]},
            }}, "Farm_1")
            bridge.save = "Farm_2"
            bridge.game_session = bridge.runtime._player_session_id = "session-b"

    bridge.runtime._chat_ws = SwitchingSocket()
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
    ws = bridge.runtime._chat_ws = ReplySocket()
    bridge.runtime._memory_store.add("OtherFarm", kind="agreement", text="保留原来的约定", source="player",
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
    assert bridge.runtime._memory_store.list("OtherFarm")["entries"][0]["text"] == "保留原来的约定"


def test_desktop_waits_for_external_partner_without_losing_native_controls(tmp_path, monkeypatch):
    monkeypatch.setenv("STARDEW_CONNECTION_SOURCE", "external")
    bridge = DesktopHarness(tmp_path)
    bridge.runtime._sessions["pi:Farm_1"] = "builtin"

    async def verify():
        transport = httpx.ASGITransport(app=create_app(bridge, "token"))
        async with httpx.AsyncClient(transport=transport, base_url="http://127.0.0.1",
                                     headers={"Authorization": "Bearer token"}) as client:
            status = (await client.get("/v1/status")).json()
            assert status["ready"] and not status["connection"]["ready"]
            game = {"gameSessionId": "session-a"}
            assert (await client.post("/v1/commands", json={**game, "requestId": "old", "text": "收菜"})).status_code == 409
            assert (await client.post("/v1/control", json={**game, "action": "handoff"})).status_code == 409
            assert (await client.post("/v1/control", json={**game, "action": "takeover"})).status_code == 200
            assert (await client.post("/v1/memory", json=game)).status_code == 200
            assert (await client.post("/v1/profile", json={**game, "expectedRevision": 0,
                                     "patch": {"personality": "calm"}})).status_code == 409
            await asyncio.gather(*bridge.turns)

    asyncio.run(verify())
    assert bridge.calls == [("takeover", {})] and not bridge.requests
    assert bridge.runtime._backend is None and bridge.runtime.get_conversation_id("Farm_1") is None


def test_desktop_requires_explicit_original_session_and_idle_confirmation(tmp_path, monkeypatch):
    monkeypatch.setenv("STARDEW_CONNECTION_SOURCE", "external")
    bridge = DesktopHarness(tmp_path)
    write_binding(tmp_path, {"adapter": "mcode-acp", "providerSessionId": "original", "cwd": str(tmp_path),
                             "originalClientIdleAcknowledged": True, "enabled": False})
    calls = []

    async def launch(**kwargs):
        calls.append(kwargs)
        return {"attached": True}

    bridge.partner.launch = launch
    executable = tmp_path / "mcode.cmd"
    executable.touch()

    async def verify():
        transport = httpx.ASGITransport(app=create_app(bridge, "token"))
        async with httpx.AsyncClient(transport=transport, base_url="http://127.0.0.1",
                                     headers={"Authorization": "Bearer token"}) as client:
            request = {"action": "attach", "cwd": str(tmp_path), "client": str(executable), "sessionId": "original"}
            assert (await client.post("/v1/partner", json=request)).status_code == 409
            request["originalClientIdle"] = True
            assert (await client.post("/v1/partner", json={**request, "sessionId": "different"})).status_code == 409
            assert (await client.post("/v1/partner", json=request)).status_code == 200

    asyncio.run(verify())
    assert [call["session"] for call in calls] == ["original"]


def test_backend_selection_keeps_provider_specific_session_and_client(tmp_path, monkeypatch):
    monkeypatch.setenv("STARDEW_CONNECTION_SOURCE", "builtin")
    codex = DesktopBridge(tmp_path, "", "selected-codex.exe", backend="codex")
    codex.runtime.record_conversation_id("farm", "codex-original")
    dsh = DesktopBridge(tmp_path, "", "selected-dsh.cmd", backend="dsh")
    dsh.runtime.record_conversation_id("farm", "dsh-original")
    mcode = DesktopBridge(tmp_path, "", "selected-mcode.cmd", backend="mcode")

    assert isinstance(codex.runtime._get_backend(), CodexBackend) and codex.runtime._get_backend().command == "selected-codex.exe"
    assert isinstance(dsh.runtime._get_backend(), DshBackend) and dsh.runtime._get_backend().model == "deepseek-flash"
    assert dsh.runtime._get_backend().command == "selected-dsh.cmd"
    assert isinstance(mcode.runtime._get_backend(), McodeBackend) and mcode.runtime._get_backend().command == "selected-mcode.cmd"
    assert mcode.runtime.get_conversation_id("farm") is None
    assert dsh.runtime.get_conversation_id("farm") == "dsh-original"
    assert codex.runtime.get_conversation_id("farm") == "codex-original"


def test_registered_provider_runs_a_game_turn_without_bridge_dispatch_changes(tmp_path, monkeypatch):
    monkeypatch.setenv("STARDEW_CONNECTION_SOURCE", "builtin")
    monkeypatch.setattr("stardew_ai_runtime.compatibility.assert_native_compatible", lambda _: None)
    calls = []

    class Provider:
        def run(self, task, session_id, prompt):
            calls.append((task.request_id, session_id, prompt))
            task.provider_input_delivered = True
            return {"success": True, "conversation_id": session_id, "response": "已观察农场。",
                    "usage": {"input_tokens": 20, "output_tokens": 8}}

        def close(self):
            calls.append("closed")

    def factory(options):
        assert options.command == "configured.exe" and options.effort == "high"
        return Provider()

    spec = ProviderSpec("fixture", "Fixture", "fixture.exe", factory, default_model="game-test",
                        desktop=True, usage_source="fixture_receipt")
    monkeypatch.setitem(PROVIDERS, spec.id, spec)
    assert next(row for row in desktop_catalogue() if row["id"] == spec.id)["defaultModel"] == "game-test"
    bridge = DesktopBridge(tmp_path, "", "configured.exe", backend=spec.id, effort="high")
    bridge._awaiting_native_control = False
    task = ActiveChatTask("turn", "Farm_1")
    result = bridge.runtime._execute_turn(task, "existing", "玩家说：看看农场")

    assert bridge.runtime.model == "game-test" and result["response"] == "已观察农场。"
    assert bridge.runtime._provider_usage(result, "turn", "Farm_1") == (result["usage"], "fixture_receipt", None)
    assert calls[0][:2] == ("turn", "existing")
    asyncio.run(bridge.runtime._close_backend())
    assert calls[-1] == "closed"


def test_dsh_desktop_installation_is_version_checked_without_cmd_shell(tmp_path, monkeypatch):
    base = tmp_path / "DeepSeek Harness"
    shim = base / "resources/runtime/cli/bin/dsh.cmd"
    shim.parent.mkdir(parents=True)
    shim.touch()
    host = base / "DeepSeek Harness.exe"
    host.touch()
    (base / "resources/app.asar").touch()
    calls = []
    monkeypatch.setattr("stardew_ai_runtime.provider_registry.subprocess.run", lambda command, **kw: calls.append((command, kw)))

    assert PROVIDERS["dsh"].check_client(tmp_path, str(shim)) == str(shim)
    command, options = calls[0]
    assert command[0] == str(host) and command[-1] == "--version"
    assert options["env"]["ELECTRON_RUN_AS_NODE"] == "1" and not options.get("shell")


def test_desktop_adopts_and_defers_current_milestone_without_taking_control(tmp_path):
    bridge = DesktopHarness(tmp_path)
    ws = bridge.runtime._chat_ws = ReplySocket()
    bridge.runtime.actor_mode = "player"
    bridge.runtime.control_owner = "human"
    bridge.runtime._latest_snapshot_payload["world"] = {"year": 1, "season": "spring", "dayOfMonth": 12, "timeOfDay": 800}
    bridge.runtime._profile_store.set(bridge.save, {"playStyle": "earn"}, 0)
    original = next(node for node in bridge.status()["milestones"] if "草莓" in node["title"])

    async def verify():
        binding = bridge.binding()
        adopted = await bridge.change_milestone(binding, "adopt", original["id"], original["updatedAt"])
        await asyncio.gather(*bridge.runtime._life_drain_tasks)
        node = next(row for row in adopted["nodes"] if row["id"] == original["id"])
        assert node["status"] == "adopted"
        assert bridge.runtime.control_owner == "human" and bridge.runtime._active_task is None
        assert bridge.runtime._pending_life_preparation[bridge.save] == {}
        assert bridge.runtime._work_store.list_todos(bridge.save)
        try:
            await bridge.change_milestone(binding, "defer", original["id"], original["updatedAt"])
        except DesktopError as error:
            assert "最新状态" in str(error)
        else:
            raise AssertionError("Stale milestone action was accepted")
        deferred = await bridge.change_milestone(binding, "defer", node["id"], node["updatedAt"])
        assert next(row for row in deferred["nodes"] if row["id"] == node["id"])["status"] == "deferred"
        assert all(todo["status"] == "cancelled" for todo in bridge.runtime._work_store.list_todos(bridge.save))

    asyncio.run(verify())
    assert bridge.calls == []
    assert any(row["messageType"] == "life.milestones.state" for row in ws.sent)


def test_desktop_closes_and_restores_one_pending_decision_without_answering_it(tmp_path):
    bridge = DesktopHarness(tmp_path)
    bridge.runtime._chat_ws = ReplySocket()
    first = bridge.runtime._work_store.add_goal(bridge.save, "照料农场", source="user")
    notice = bridge.runtime._work_store.request_player_decision(bridge.save, first.id, "先浇水还是收菜？", ["浇水", "收菜"])
    other = bridge.runtime._work_store.request_player_decision(bridge.save, first.id, "种什么作物？")

    async def verify():
        transport = httpx.ASGITransport(app=create_app(bridge, "token"))
        async with httpx.AsyncClient(transport=transport, base_url="http://127.0.0.1",
                                     headers={"Authorization": "Bearer token"}) as client:
            body = {"gameSessionId": "old-session", "action": "dismiss", "noticeId": notice["id"]}
            assert (await client.post("/v1/notices", json=body)).status_code == 409
            body["gameSessionId"] = "session-a"
            assert (await client.post("/v1/notices", json=body)).status_code == 200
            snapshot = bridge.status()
            assert [row["id"] for row in snapshot["decisions"]] == [other["id"]]
            assert snapshot["closedDecisions"][0]["id"] == notice["id"]
            assert (await client.post("/v1/notices", json={**body, "action": "restore"})).status_code == 200
            assert bridge.status()["decisions"][0]["choices"] == ["浇水", "收菜"]
            assert not any(row.get("answeredAt") for row in bridge.runtime._work_store.state(bridge.save).notices)
            bridge.runtime._work_store.answer_player_notice(bridge.save, notice["id"], "浇水", "other-client")
            assert (await client.post("/v1/notices", json=body)).status_code == 409

    asyncio.run(verify())
    assert bridge.calls == []
