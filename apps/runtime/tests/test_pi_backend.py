import json
import sys
import threading
from pathlib import Path

import pytest

from stardew_ai_runtime.adapters.providers.pi import PiBackend
from stardew_ai_runtime.chat_backend_config import load_chat_backend_config
from stardew_ai_runtime.chat_bridge import ActiveChatTask, ChatBridge

FAKE = Path(__file__).parent / "support/fake_pi.py"


def backend(tmp_path, monkeypatch, mode="normal", **kwargs):
    monkeypatch.setenv("PYTHONIOENCODING", "utf-8")
    monkeypatch.setenv("FAKE_PI_MODE", mode)
    monkeypatch.setenv("FAKE_PI_LOG", str(tmp_path / "rpc.jsonl"))
    return PiBackend(tmp_path, command=[sys.executable, str(FAKE)], **kwargs)


def test_rpc_restores_exact_session_delivers_unicode_and_refreshes_mcp_authority(tmp_path, monkeypatch):
    monkeypatch.setenv("STARDEW_DECISION_TOKEN", "first-lease")
    instance = backend(tmp_path, monkeypatch)
    task = ActiveChatTask("first", "S")
    result = instance.run(task, None, "农场交给你，保留幼树。\u2028先观察。")
    assert result["success"] and task.provider_input_delivered
    assert result["response"] == "已核对菜地。\u2028继续照料。"
    assert result["usage"]["total_tokens"] == 88
    assert task.process is None
    session = result["conversation_id"]
    monkeypatch.setenv("STARDEW_DECISION_TOKEN", "next-lease")
    resumed = instance.run(ActiveChatTask("next", "S"), session, "接着做")
    assert resumed["success"] and resumed["conversation_id"] == session
    frames = [json.loads(line) for line in (tmp_path / "rpc.jsonl").read_text(encoding="utf-8").split("\n") if line]
    assert [f["type"] for f in frames] == ["get_state", "prompt", "get_state", "prompt"]
    assert frames[1]["message"] == "农场交给你，保留幼树。\u2028先观察。"
    config = json.loads((tmp_path / "data/pi/.pi/mcp.json").read_text(encoding="utf-8"))
    server = config["mcpServers"]["stardew-companion"]
    assert server["env"]["STARDEW_DECISION_TOKEN"] == "next-lease"
    assert server["exposure"] == "direct" and server["args"][-1] == "light"


def test_agent_end_is_not_the_rpc_completion_boundary(tmp_path, monkeypatch):
    result = backend(tmp_path, monkeypatch, "retry").run(ActiveChatTask("R", "S"), None, "继续")
    assert result["success"] and result["response"] == "恢复后继续照料。"
    assert result["usage"]["generations_count"] == 2


def test_productive_events_extend_silent_deadline(tmp_path, monkeypatch):
    # One run lasts longer than its silence allowance while tool events progress.
    result = backend(tmp_path, monkeypatch, "progress", timeout_seconds=0.3).run(
        ActiveChatTask("R", "S"), None, "观察")
    assert result["success"] and result["duration"] > 0.3


def test_compaction_rebuilds_next_turn_context_without_replacing_session(tmp_path, monkeypatch):
    monkeypatch.setattr("stardew_ai_runtime.compatibility.assert_native_compatible", lambda _: None)
    instance = backend(tmp_path, monkeypatch, "compact")
    bridge = ChatBridge(run_dir=tmp_path, backend="pi", enable_plan_worker=False)
    bridge._backend = instance
    bridge._latest_snapshot_payload = {"world": {"description": "农场当前状态" * 500}}
    first = bridge._execute_turn(ActiveChatTask("first", "S"), None, "实时上下文：{}\n玩家说：查看农场")
    assert first["success"] and first["context_compacted"]
    monkeypatch.setenv("FAKE_PI_MODE", "normal")
    second = bridge._execute_turn(ActiveChatTask("second", "S"), first["conversation_id"], "实时上下文：{}\n玩家说：继续")
    assert second["success"] and second["conversation_id"] == first["conversation_id"]
    frames = [json.loads(line) for line in (tmp_path / "rpc.jsonl").read_text(encoding="utf-8").split("\n") if line]
    prompts = [f["message"] for f in frames if f["type"] == "prompt"]
    assert "实时上下文：" in prompts[1] and "上下文增量：" not in prompts[1]


@pytest.mark.parametrize(("mode", "error"), [("silent", "TIMEOUT"), ("unconfirmed", "PI_INPUT_UNCONFIRMED"),
                                               ("error", "PI_TURN_FAILED"), ("invalid", "PI_RPC_INVALID")])
def test_rpc_failures_do_not_claim_unconfirmed_input(tmp_path, monkeypatch, mode, error):
    task = ActiveChatTask("R", "S")
    result = backend(tmp_path, monkeypatch, mode, timeout_seconds=0.5).run(task, None, "继续")
    assert not result["success"] and result["error"] == error
    assert task.process is None
    if mode in {"silent", "unconfirmed"}:
        assert not task.provider_input_delivered


def test_missing_or_changed_session_never_prompts_a_substitute(tmp_path, monkeypatch):
    instance = backend(tmp_path, monkeypatch)
    missing = str(tmp_path / "missing.jsonl")
    assert instance.run(ActiveChatTask("R", "S"), missing, "继续")["error"] == "PI_SESSION_UNAVAILABLE"
    assert not (tmp_path / "rpc.jsonl").exists()
    existing = tmp_path / "original.jsonl"
    existing.touch()
    monkeypatch.setenv("FAKE_PI_MODE", "mismatch")
    task = ActiveChatTask("R", "S")
    result = instance.run(task, str(existing), "继续")
    assert result["error"] == "PI_SESSION_CHANGED" and result["conversation_id"] == str(existing)
    assert not task.provider_input_delivered
    frames = [json.loads(line) for line in (tmp_path / "rpc.jsonl").read_text().splitlines()]
    assert not any(f["type"] == "prompt" for f in frames)


def test_fresh_provider_exit_does_not_bind_uncreated_session_file(tmp_path, monkeypatch):
    task = ActiveChatTask("first", "S")
    instance = backend(tmp_path, monkeypatch, "exit_before_first_save")
    result = instance.run(task, None, "开始")
    assert result["error"] == "PI_RUNTIME_EXITED"
    assert result["conversation_id"] is None
    assert task.provider_conversation_id is None
    monkeypatch.setenv("FAKE_PI_MODE", "normal")
    assert instance.run(ActiveChatTask("retry", "S"), None, "再试一次")["success"]


def test_cancel_requests_rpc_abort_and_releases_owned_process(tmp_path, monkeypatch):
    task = ActiveChatTask("R", "S")
    instance = backend(tmp_path, monkeypatch, "cancel")
    instance.progress = lambda event: setattr(task, "cancelled", True) if event.kind == "tool_execution_start" else None
    result = instance.run(task, None, "继续")
    assert result["error"] == "PLAYER_CANCELLED" and task.process is None
    frames = [json.loads(line) for line in (tmp_path / "rpc.jsonl").read_text().splitlines()]
    assert any(f["type"] == "abort" for f in frames)
    assert not any(t.name.startswith("pi-rpc-") and t.is_alive() for t in threading.enumerate())


def test_pi_configuration_dispatch_preserves_client_model_defaults(tmp_path, monkeypatch):
    config = tmp_path / "backend.json"
    config.write_text('{"backend":"pi"}', encoding="utf-8")
    monkeypatch.setenv("STARDEW_CHAT_BACKEND_CONFIG", str(config))
    assert load_chat_backend_config() == {"backend": "pi", "model": ""}
    bridge = ChatBridge(run_dir=tmp_path, enable_plan_worker=False)
    assert isinstance(bridge._get_backend(), PiBackend)
    assert bridge.model == "" and bridge.effort == "default"
