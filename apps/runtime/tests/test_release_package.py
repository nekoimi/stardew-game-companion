"""Release integrity and registration boundaries; no game or account access."""
import json
import subprocess
import sys
from pathlib import Path

import pytest
from support.release_fixture import package_at

from stardew_ai_runtime import compatibility
from stardew_ai_runtime.release_package import managed_path, verify_release

ROOT = Path(__file__).resolve().parents[3]


def test_relocated_release_pairs_without_developer_metadata(tmp_path, monkeypatch):
    root = tmp_path / "搬迁 空格" / "StardewAI.Companion.Mod"
    package_at(root)
    monkeypatch.setattr(compatibility, "__file__", str(root / "runtime/src/stardew_ai_runtime/compatibility.py"))
    compatibility.assert_native_compatible(root)
    with pytest.raises(compatibility.CompatibilityError, match="MOD_RUNTIME_MISMATCH"):
        compatibility.assert_native_compatible(tmp_path / "other-mod")
    (root / "runtime/src/stardew_ai_runtime/chat_bridge.py").write_text("changed")
    with pytest.raises(compatibility.CompatibilityError, match="MOD_RUNTIME_MISMATCH"):
        compatibility.assert_native_compatible(root)


@pytest.mark.skipif(sys.platform != "win32", reason="Windows release registration")
def test_codex_registration_from_release_is_local_and_preserves_configuration(tmp_path):
    import tomllib

    root = tmp_path / "玩家发行包"
    package_at(root)
    project = tmp_path / "我的农场"
    config = project / ".codex/config.toml"
    config.parent.mkdir(parents=True)
    original = 'model = "existing-choice"\n[mcp_servers.other]\ncommand = "other.exe"\n'
    config.write_text(original, encoding="utf-8")
    command = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
               str(root / "tools/register-mcp.ps1"), "-RunDir", str(root), "-Agent", "codex",
               "-ProjectDir", str(project), "-Install"]
    for _ in range(2):
        result = subprocess.run(command, capture_output=True)
        assert result.returncode == 0, result.stderr.decode("utf-8", errors="replace")
    assert config.read_text(encoding="utf-8").startswith(original)
    data = tomllib.loads(config.read_text(encoding="utf-8"))
    server = data["mcp_servers"]["stardew-companion"]
    assert server["command"] == str(root / "runtime/python/python.exe")
    assert server["env"] == {"STARDEW_EXTERNAL_CODEX": "1"}
    assert server["args"][4] == str(root)
    assert (project / ".agents/skills/stardew-companion/SKILL.md").is_file()
    # A hand-owned collision must leave every existing byte untouched.
    config.write_text('[mcp_servers."stardew-companion"]\ncommand = "mine.exe"\n', encoding="utf-8")
    before = config.read_bytes()
    assert subprocess.run(command, capture_output=True).returncode != 0
    assert config.read_bytes() == before


@pytest.mark.parametrize("relative", ["../outside", "/outside", "C:/outside", "data/memory.json", "config/chat-backend.json", ".kimi-code/mcp.json"])
def test_manifest_cannot_escape_or_own_player_data(tmp_path, relative):
    with pytest.raises(ValueError):
        managed_path(tmp_path, relative)


def test_manifest_cannot_omit_native_pairing(tmp_path):
    doc = package_at(tmp_path)
    doc["modSha256"] = "0" * 64
    (tmp_path / "release-manifest.json").write_text(json.dumps(doc))
    with pytest.raises(ValueError, match="pairing"):
        verify_release(tmp_path)


@pytest.mark.parametrize("desktop", [False, True])
def test_normal_pairing_reads_only_required_files_but_explicit_verify_audits_all(tmp_path, monkeypatch, desktop):
    from stardew_ai_runtime.release_package import DESKTOP_REQUIRED, REQUIRED

    doc = package_at(tmp_path, desktop=desktop)
    doc["files"].append({"path": "runtime/python/unused-package-file.txt", "sha256": "0" * 64})
    (tmp_path / "release-manifest.json").write_text(json.dumps(doc))
    read = []
    original = Path.read_bytes

    def counted(path):
        read.append(path.relative_to(tmp_path).as_posix())
        return original(path)

    monkeypatch.setattr(Path, "read_bytes", counted)
    verify_release(tmp_path, full=False)
    required = DESKTOP_REQUIRED if desktop else REQUIRED
    assert set(read) == required and len(read) == len(required)
    with pytest.raises(ValueError, match="missing"):
        verify_release(tmp_path)
    (tmp_path / "StardewAI.Companion.Mod.dll").write_bytes(b"mismatch")
    with pytest.raises(ValueError, match="changed"):
        verify_release(tmp_path, full=False)


def test_builder_allowlists_production_inputs():
    """A DLL folder may contain game/test outputs; the packager only lists production files."""
    import importlib.util

    spec = importlib.util.spec_from_file_location("build_release", ROOT / "packaging/windows/build-release.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    assert set(module.MOD_FILES) == {"manifest.json", "StardewAI.Companion.Mod.dll", "StardewAI.Companion.Mod.deps.json"}


def test_builder_removes_machine_bound_bin_launchers_without_touching_libraries(tmp_path):
    import csv
    import importlib.util

    spec = importlib.util.spec_from_file_location("build_release", ROOT / "packaging/windows/build-release.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    python = tmp_path / "python.exe"
    site = tmp_path / "Lib/site-packages"
    metadata = site / "fixture.dist-info"
    metadata.mkdir(parents=True)
    (metadata / "entry_points.txt").write_text("[console_scripts]\nmcp = mcp.cli:app\n", encoding="utf-8")
    (site / "bin").mkdir()
    wrapper = site / "bin/mcp.exe"
    wrapper.write_bytes(b"MZ-launcher\n#!" + str(python.resolve()).encode("utf-8") + b"\nPK-main")
    library = site / "bin/native-helper.dll"
    library.write_bytes(b"native dependency")
    record = metadata / "RECORD"
    record.write_text("bin/mcp.exe,sha256=machine-specific,123\nbin/native-helper.dll,sha256=stable,17\n"
                      "fixture.dist-info/RECORD,,\n", encoding="utf-8")
    module.remove_machine_bound_scripts(site, python)
    assert not wrapper.exists()
    assert library.read_bytes() == b"native dependency"
    with record.open(encoding="utf-8", newline="") as stream:
        assert list(csv.reader(stream)) == [["bin/native-helper.dll", "sha256=stable", "17"],
                                         ["fixture.dist-info/RECORD", "", ""]]
