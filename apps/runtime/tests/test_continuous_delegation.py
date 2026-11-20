import asyncio
from datetime import UTC, datetime

import pytest
from mcp.server.fastmcp.exceptions import ToolError
from support.mcp_scheduler import mock_scheduler  # noqa: F401

from stardew_ai_runtime.chat_bridge import ChatBridge
from stardew_ai_runtime.game_tools import GAME_TOOLS
from stardew_ai_runtime.mcp_server import create_mcp_server
from stardew_ai_runtime.protocol import Envelope
from stardew_ai_runtime.work_state import WorkStateError, WorkStore


def delegated_bridge(tmp_path):
    bridge = ChatBridge(run_dir=tmp_path, backend="pi", enable_plan_worker=False)
    store = bridge._work_store
    store.record_request("S", "original", "农场交给你持续经营，发展种植，保留幼树。")
    store.begin_decision("S", "lease", request_id="original")
    goal = store.delegate_current_request("S", "lease", "先照料现有作物，再结合资源安排补种")
    bridge._autonomy.set_enabled("S", False, goal_scope=goal.id)
    bridge._latest_snapshot_payload = {
        "world": {"year": 1, "season": "spring", "dayOfMonth": 1, "timeOfDay": 900},
        "companion": {"locationId": "Farm", "tileX": 1, "tileY": 2, "stamina": 200},
        "farmWork": {}, "inventory": {"slots": []},
    }
    return bridge, goal


def prepared(bridge):
    envelope = Envelope("0.1", "world.snapshot", "snapshot", "mod", 1, 1, datetime.now(UTC),
                        bridge._latest_snapshot_payload, save_id="S", game_session_id="game")
    return bridge._prepare_autonomy_decision(envelope, "S")


def test_delegation_binds_original_request_and_survives_reload(tmp_path):
    bridge, goal = delegated_bridge(tmp_path)
    store = bridge._work_store
    assert goal.source == "user" and goal.kind == "main"
    assert goal.text == "农场交给你持续经营，发展种植，保留幼树。"
    assert store.delegate_current_request("S", "lease", "按现场灵活经营").id == goal.id
    restored = WorkStore(store.state_path)
    assert len(restored.state("S").goals) == 1
    assert restored.state("S").requests[0]["goalId"] == goal.id
    assert not restored.state("S").tasks
    with pytest.raises(WorkStateError, match="STALE_DECISION"):
        restored.delegate_current_request("S", "old-lease", "不应接管")
    restored.record_request("S", "auto", "模型自己的建议", origin="autonomy")
    restored.begin_decision("S", "auto-lease", request_id="auto")
    with pytest.raises(WorkStateError, match="PLAYER_DELEGATION_REQUIRED"):
        restored.delegate_current_request("S", "auto-lease", "自行创建委托")


def test_native_wait_blocks_planning_until_due_then_continues_without_user_input(tmp_path):
    bridge, goal = delegated_bridge(tmp_path)
    bridge._work_store.set_goal_continuation("S", "lease", "wait", "等九点半开门", {
        "type": "gameTime", "year": 1, "season": "spring", "day": 1, "timeOfDay": 930})
    assert prepared(bridge) is None
    bridge._latest_snapshot_payload["world"]["timeOfDay"] = 930
    first = prepared(bridge)
    assert first is not None
    bridge._autonomy.record_world_event("S", first[0], 1)
    assert prepared(bridge) is None, "Identical snapshots must not hot-loop"
    bridge._work_store.set_goal_continuation("S", "lease", "wait", "换句话说等九点半", {
        "type": "gameTime", "year": 1, "season": "spring", "day": 1, "timeOfDay": 930})
    assert prepared(bridge) is None, "Rewording a continuation must not wake the model"
    bridge._latest_snapshot_payload["world"]["dayOfMonth"] = 2
    bridge._latest_snapshot_payload["world"]["timeOfDay"] = 600
    assert prepared(bridge) is not None, "Native day changes wake the saved delegation"
    assert bridge._work_store.state("S").goals[0].id == goal.id


def test_silent_turns_have_bounded_continuations_and_native_progress_rearms_them(tmp_path):
    bridge, _ = delegated_bridge(tmp_path)
    for _ in range(6):
        bridge._rearm_after_standby_turn("S")
    epoch = bridge._autonomy.state("S").decision_epoch
    bridge._rearm_after_standby_turn("S")
    assert bridge._autonomy.state("S").decision_epoch == epoch
    bridge._latest_snapshot_payload["companion"]["tileX"] = 2
    bridge._rearm_after_standby_turn("S")
    assert bridge._autonomy.state("S").decision_epoch > epoch
    bridge._work_store.set_goal_continuation("S", "lease", "wait", "待明日检查", {
        "type": "calendar", "year": 1, "season": "spring", "day": 2})
    epoch = bridge._autonomy.state("S").decision_epoch
    bridge._rearm_after_standby_turn("S")
    assert bridge._autonomy.state("S").decision_epoch == epoch


def test_choices_pause_takeover_and_cancellation_gate_autonomous_work(tmp_path):
    bridge, goal = delegated_bridge(tmp_path)
    notice = bridge._work_store.request_player_decision("S", goal.id, "是否更改保留区域？")
    assert prepared(bridge) is None
    bridge._work_store.set_notice_visibility("S", [notice["id"]], True)
    assert prepared(bridge) is None, "Hiding a question must not authorize continuation"
    bridge._work_store.set_notice_visibility("S", [notice["id"]], False)
    bridge._work_store.answer_player_notice("S", notice["id"], "不改，按原约定", "answer")
    assert prepared(bridge) is not None
    bridge._work_store.set_paused("S", True)
    assert prepared(bridge) is None
    bridge._work_store.set_paused("S", False)
    bridge.actor_mode, bridge.control_owner = "player", "human"
    assert prepared(bridge) is None
    bridge.control_owner = "agent"
    assert prepared(bridge) is not None
    bridge._work_store.cancel_goal("S", goal.id)
    assert prepared(bridge) is None


def test_waiting_for_tomorrow_does_not_prevent_native_sleep_planning(tmp_path):
    bridge, _ = delegated_bridge(tmp_path)
    bridge.actor_mode, bridge.control_owner = "player", "agent"
    bridge._work_store.set_goal_continuation("S", "lease", "wait", "明日照料", {
        "type": "calendar", "year": 1, "season": "spring", "day": 2})
    assert prepared(bridge) is None
    bridge._latest_snapshot_payload["world"]["timeOfDay"] = 2330
    assert prepared(bridge) is not None


def test_delegation_tools_keep_eight_entry_points_and_use_current_request(tmp_path, monkeypatch, mock_scheduler):  # noqa: F811
    store = WorkStore(tmp_path / "data/work-state.json")
    sid = "mock-save-123"
    store.record_request(sid, "player", "以后农场交给你照料，先发展种植。")
    store.begin_decision(sid, "lease", request_id="player")
    monkeypatch.setenv("STARDEW_DECISION_TOKEN", "lease")
    mock_scheduler.run_dir = str(tmp_path)

    async def run():
        server = create_mcp_server(run_dir=tmp_path, scheduler=mock_scheduler, surface="light")
        assert {t.name for t in await server.list_tools()} == GAME_TOOLS
        _, result = await server.call_tool("manage_arrangements", {"change": {
            "delegate_goal": {"summary": "照料现有作物，再按资源扩种"}}})
        goal = result["goal"]
        assert goal["text"] == "以后农场交给你照料，先发展种植。"
        assert goal["constraints"]["playerRequestId"] == "player"
        _, waiting = await server.call_tool("manage_arrangements", {"change": {
            "set_goal_continuation": {"status": "wait", "reason": "等补水恢复", "trigger": {
                "type": "resource", "resource": "water", "minAmount": 10}}}})
        assert waiting["goalId"] == goal["id"]
        store.reload()
        assert store.state(sid).goals[0].project["continuation"]["status"] == "wait"
        assert not store.state(sid).tasks

        notice = store.request_player_decision(sid, goal["id"], "是否出售保留物品？")
        with pytest.raises(ToolError, match="PLAYER_DECISION_REQUIRED"):
            await server.call_tool("submit_work", {"action": {"navigate_to": {
                "location_id": "Farm", "tile": {"x": 1, "y": 2}}}})
        mock_scheduler.execute_navigate_to.assert_not_called()
        store.answer_player_notice(sid, notice["id"], "保留，先去菜地", "answer")
        _, accepted = await server.call_tool("submit_work", {"action": {"navigate_to": {
            "location_id": "Farm", "tile": {"x": 1, "y": 2}}}})
        assert "PLAYER_DECISION_REQUIRED" not in str(accepted)
        mock_scheduler.execute_navigate_to.assert_called_once()

    asyncio.run(run())
