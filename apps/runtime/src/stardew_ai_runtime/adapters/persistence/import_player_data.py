"""Offline import of player data into a Windows desktop companion package."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import tempfile
from pathlib import Path

from ..providers.pi import resolve_pi_session
from .json_file import read_json
from .json_file import replace_json as _write_object
from .state_paths import RUNTIME_FILES, SESSION_BINDINGS

PENDING = ".desktop-import-pending"
MOD_ID = "CSXizhang.StardewAICompanion"


def _read_object(path: Path) -> dict:
    try:
        value = read_json(path, encoding="utf-8-sig")
    except FileNotFoundError:
        return {}
    except ValueError as ex:
        raise ValueError(f"文件无法读取：{path.name}。") from ex
    if not isinstance(value, dict):
        raise ValueError(f"文件格式无效：{path.name}。")
    return value


def _local(root: Path, relative: str | Path) -> Path:
    path = root / relative
    if not path.is_relative_to(root) or path.resolve() != path or path == root:
        raise ValueError("导入路径包含链接或指向安装目录外。")
    return path


def _digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def _stage_sessions(source: Path, stage: Path) -> int:
    source_config = {key.casefold(): value for key, value in _read_object(_local(source, "config.json")).items()}
    if str(source_config.get("connectionsource", "builtin")).casefold() == "external":
        raise ValueError("当前桌面版无法接续外部伙伴会话，可明确选择仅导入数据。")
    desktop = source.parent.parent
    source_is_desktop = (desktop / "星露谷游戏伴侣.exe").is_file()
    if source_is_desktop:
        settings = _read_object(_local(desktop, "data/desktop-settings.json"))
        configured = settings.get("Backend", "pi")
    else:
        configured = _read_object(_local(source, "config/chat-backend.json")).get("backend")
    if configured and configured != "pi":
        raise ValueError("原安装使用其他模型后端，可明确选择仅导入数据。")

    bindings = _read_object(_local(source, SESSION_BINDINGS))
    pi_keys = {key: value for key, value in bindings.items() if key.startswith(("pi:", "life:pi:"))}
    if not source_is_desktop and configured != "pi" and len(pi_keys) != len(bindings):
        raise ValueError("无法确认原安装使用 Pi，可明确选择仅导入数据。")
    if not pi_keys:
        return 0
    aliases = {}
    sessions = _local(source, "data/pi/sessions")
    for session_id in pi_keys.values():
        if not isinstance(session_id, str) or not session_id.strip():
            raise ValueError("旧 Pi 会话绑定无效，导入尚未写入。")
        if session_id in aliases:
            continue
        try:
            path = resolve_pi_session(source, session_id).resolve()
            if not path.is_relative_to(sessions) or path.suffix.casefold() != ".jsonl":
                raise ValueError("Session is outside the source package")
            with path.open(encoding="utf-8-sig") as stream:
                header = json.loads(stream.readline())
            if not isinstance(header, dict) or header.get("type") != "session" or not header.get("id"):
                raise ValueError("Invalid Pi session header")
        except (OSError, ValueError) as ex:
            raise ValueError("原 Pi 会话缺失或无法读取，导入尚未写入。可修复来源或明确选择仅导入数据。") from ex
        relative = path.relative_to(sessions)
        destination = stage / "data/pi/sessions" / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, destination)
        aliases[session_id] = relative.as_posix()
    _write_object(stage / "data/pi/session-imports.json", aliases)
    _write_object(stage / SESSION_BINDINGS, bindings)
    return len(aliases)


def _stage_data(source: Path, stage: Path) -> int:
    data = _local(source, "data")
    candidates = [data / name for name in RUNTIME_FILES]
    candidates += list(_local(source, "data/conversations").glob("*.json"))
    candidates += list(data.glob("*/companion-actor-*.json"))
    for path in candidates:
        _local(source, path.relative_to(source))
        if not path.is_file():
            continue
        _read_object(path)
        destination = stage / path.relative_to(source)
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, destination)
    return len(list(stage.rglob("*.json")))


def _apply_pending(target: Path, pending: Path) -> dict:
    plan = _read_object(pending / "plan.json")
    files = plan.get("files")
    if not isinstance(files, dict) or not files:
        raise ValueError("未完成的导入计划无效。")
    # Validate all conflicts first. A file moved before an interruption is
    # accepted on retry only if its original bytes are still present.
    moves = []
    for relative, digest in files.items():
        path = _local(target, relative)
        staged = _local(pending / "files", relative)
        if path.exists():
            if not path.is_file() or _digest(path) != digest:
                raise ValueError(f"目标已有不同的 {path.name}，无法合并。")
        elif staged.is_file() and _digest(staged) == digest:
            moves.append((staged, path))
        else:
            raise ValueError(f"待导入文件缺失或损坏：{path.name}。")
    for staged, path in moves:
        path.parent.mkdir(parents=True, exist_ok=True)
        staged.rename(path)
    # Mark completion in one operation before removing disposable staging.
    with tempfile.TemporaryDirectory(prefix="desktop-import-complete-", dir=target, ignore_cleanup_errors=True) as completed:
        pending.rename(Path(completed) / "completed")
    return {key: plan[key] for key in ("dataFiles", "sessions")}


def import_data(target: Path, source: Path | None = None, *, data_only: bool = False) -> dict:
    target = target.resolve()
    pending = _local(target, PENDING)
    if pending.exists():
        return _apply_pending(target, pending)
    if source is None:
        raise ValueError("请选择原伙伴 Mod 目录。")
    source = source.resolve()
    if source.is_relative_to(target) or target.is_relative_to(source):
        raise ValueError("请选择另一份伙伴安装。")
    if _read_object(_local(source, "manifest.json")).get("UniqueID") != MOD_ID:
        raise ValueError("请选择含有伙伴 manifest.json 的原 Mod 目录。")
    if _read_object(_local(target, "manifest.json")).get("UniqueID") != MOD_ID:
        raise ValueError("绿色包不完整，请重新解压。")

    with tempfile.TemporaryDirectory(prefix="desktop-import-", dir=target) as temp:
        prepared = Path(temp) / "prepared"
        stage = prepared / "files"
        data_files = _stage_data(source, stage)
        sessions = 0 if data_only else _stage_sessions(source, stage)
        files = {path.relative_to(stage).as_posix(): _digest(path) for path in stage.rglob("*") if path.is_file()}
        if not files:
            raise ValueError("原安装中没有可导入的数据或会话。")
        for relative, digest in files.items():
            path = _local(target, relative)
            if path.exists() and (not path.is_file() or _digest(path) != digest):
                raise ValueError(f"目标已有不同的 {path.name}。请使用新解压的绿色包导入。")
        _write_object(prepared / "plan.json", {"files": files, "dataFiles": data_files, "sessions": sessions})
        prepared.rename(pending)
    return _apply_pending(target, pending)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", type=Path, required=True)
    parser.add_argument("--source", type=Path)
    parser.add_argument("--data-only", action="store_true")
    args = parser.parse_args()
    try:
        result = import_data(args.target, args.source, data_only=args.data_only)
    except (OSError, ValueError) as ex:
        print(json.dumps({"error": str(ex)}))
        return 1
    print(json.dumps(result))
    return 0
