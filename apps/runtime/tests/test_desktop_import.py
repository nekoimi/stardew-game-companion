import json
import shutil
import sys
from pathlib import Path

import pytest

from stardew_ai_runtime.adapters.persistence.import_player_data import MOD_ID, PENDING, import_data
from stardew_ai_runtime.adapters.providers.pi import PiBackend, resolve_pi_session
from stardew_ai_runtime.chat_bridge import ActiveChatTask
from stardew_ai_runtime.companion_memory import resolve_companion_id

FAKE_PI = Path(__file__).parent / "support/fake_pi.py"


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    return path


@pytest.fixture
def installations(tmp_path):
    source = tmp_path / "original"
    target = tmp_path / "portable"
    for root in (source, target):
        write(root / "manifest.json", {"UniqueID": MOD_ID})
    return source, target


def test_import_preserves_pi_identity_and_resumes_the_local_copy_after_relocation(installations, monkeypatch):
    source, target = installations
    write(source / "config/chat-backend.json", {"backend": "pi"})
    write(source / "config.json", {"ConnectionSource": "builtin"})
    session = write(source / "data/pi/sessions/original.jsonl", {"type": "session", "id": "original"})
    bindings = {"pi:Farm_1": str(session)}
    write(source / "chat_sessions.json", bindings)
    memory = write(source / "data/companion-memory.json", {"Farm_1": {"memoryRevision": 3, "entries": []}})
    write(source / "data/conversations/sample.json.history.json", {"SaveId": "Farm_1", "Entries": []})
    write(source / "data/transport-discovery.json", {"sessionToken": "old-token"})
    write(source / "data/pi/.pi/mcp.json", {"mcpServers": {"test": {"command": "old-path"}}})
    original_identity = resolve_companion_id(source, "Farm_1", provider="pi")

    assert import_data(target, source) == {"dataFiles": 2, "sessions": 1}
    assert session.is_file()
    assert not (target / "data/transport-discovery.json").exists()
    assert not (target / "data/pi/.pi/mcp.json").exists()
    moved = target.with_name("relocated")
    target.rename(moved)
    shutil.move(source, source.with_name("source-offline"))
    assert (moved / "data/companion-memory.json").read_bytes() == (source.with_name("source-offline") / memory.relative_to(source)).read_bytes()
    assert (moved / "data/conversations/sample.json.history.json").is_file()
    assert resolve_companion_id(moved, "Farm_1", provider="pi") == original_identity
    assert resolve_pi_session(moved, str(session)) == moved / "data/pi/sessions/original.jsonl"

    monkeypatch.setenv("PYTHONIOENCODING", "utf-8")
    monkeypatch.setenv("FAKE_PI_LOG", str(moved / "rpc.jsonl"))
    backend = PiBackend(moved, command=[sys.executable, str(FAKE_PI)], local_sessions_only=True)
    task = ActiveChatTask("continued", "Farm_1")
    result = backend.run(task, str(session), "继续照料农场")
    assert result["success"] and task.provider_input_delivered
    assert result["conversation_id"] == task.provider_conversation_id == str(session)

    (moved / "data/pi/sessions/original.jsonl").unlink()
    assert backend.run(ActiveChatTask("missing", "Farm_1"), str(session), "继续")["error"] == "PI_SESSION_UNAVAILABLE"
    assert not (moved / "data/pi/sessions/original.jsonl").exists()
    (moved / "data/pi/session-imports.json").unlink()
    source.with_name("source-offline").rename(source)
    assert backend.run(ActiveChatTask("no-alias", "Farm_1"), str(session), "继续")["error"] == "PI_SESSION_UNAVAILABLE"


def test_import_requires_explicit_data_only_for_other_backends_and_never_overwrites(installations):
    source, target = installations
    profile = Path("data/companion-profile.json")
    write(source / "config/chat-backend.json", {"backend": "dsh"})
    write(source / "chat_sessions.json", {"dsh:Farm_1": "original"})
    write(source / profile, {"Farm_1": {"companionName": "原伙伴"}})
    write(source / "data/work-state.json", {"Farm_1": {"goals": []}})
    with pytest.raises(ValueError, match="其他模型后端"):
        import_data(target, source)
    existing = write(target / profile, {"Farm_1": {"companionName": "目标包伙伴"}})
    with pytest.raises(ValueError, match="目标已有不同"):
        import_data(target, source, data_only=True)
    assert json.loads(existing.read_text(encoding="utf-8"))["Farm_1"]["companionName"] == "目标包伙伴"
    assert not (target / "data/work-state.json").exists()
    existing.unlink()
    assert import_data(target, source, data_only=True) == {"dataFiles": 2, "sessions": 0}
    assert not (target / "chat_sessions.json").exists()


def test_interrupted_import_retries_remaining_files_without_source(installations, monkeypatch):
    source, target = installations
    write(source / "data/companion-profile.json", {"Farm_1": {"companionName": "原伙伴"}})
    write(source / "data/work-state.json", {"Farm_1": {"goals": []}})
    rename = Path.rename
    moved = []

    def interrupt_second_move(old, new):
        if old.parent.parent.name == "files":
            if moved:
                raise PermissionError("simulated interrupted write")
            moved.append(new)
        return rename(old, new)

    with monkeypatch.context() as patch:
        patch.setattr(Path, "rename", interrupt_second_move)
        with pytest.raises(PermissionError):
            import_data(target, source)
    assert len(moved) == 1 and moved[0].is_file()
    assert (target / PENDING).is_dir()
    assert import_data(target) == {"dataFiles": 2, "sessions": 0}
    assert (target / "data/companion-profile.json").is_file() and (target / "data/work-state.json").is_file()
    assert not (target / PENDING).exists()
