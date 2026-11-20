"""Model client metadata and adapter assembly shared by the runtime and desktop."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol

from .agent_backends import AgyBackend, CodexBackend, DshBackend, KimiBackend, McodeBackend
from .codex_usage import codex_turn_usage
from .pi_backend import PiBackend


class Provider(Protocol):
    def run(self, active_task, session_id: str | None, prompt: str) -> dict[str, Any]: ...


@dataclass(frozen=True)
class ProviderOptions:
    run_dir: Path | None
    cwd: Path
    model: str
    effort: str
    command: str
    progress: Callable | None = None
    settings: dict[str, Any] | None = None
    local_sessions_only: bool = False
    legacy_execute: Callable | None = None

    def directory(self) -> Path:
        if self.run_dir is None:
            raise ValueError("This backend requires a game run directory")
        return self.run_dir


class ClientError(ValueError):
    pass


@dataclass(frozen=True)
class ProviderSpec:
    id: str
    name: str
    executable: str
    factory: Callable[[ProviderOptions], Provider]
    default_model: str = ""
    default_effort: str = "default"
    # Ordered supported values for config validation and desktop presentation.
    efforts: tuple[str, ...] = ("default", "low", "medium", "high")
    desktop: bool = False
    npm_shim: bool = False
    locations: tuple[str, ...] = ()
    version_command: Callable[[Path, str], tuple[list[str], dict[str, str] | None]] | None = None
    usage_projection: Callable[[dict], dict | None] | None = None
    usage_source: str = "unknown"
    missing_usage: str = "response_usage_unavailable"
    recover_usage: Callable[[Path], None] | None = None

    def receipt(self, result: dict, expense_fallback: Callable[[], dict | None] | None = None) -> tuple[dict | None, str, str | None]:
        usage = self.usage_projection(result) if self.usage_projection else result.get("usage")
        if usage is None and expense_fallback is not None:
            usage = expense_fallback()
        source = result.get("usage_source") or (self.usage_source if usage else "unknown")
        return usage, source, None if usage else (result.get("usage_limits") or self.missing_usage)

    def find_client(self, explicit: str = "") -> str:
        if explicit:
            path = Path(explicit)
            if not path.is_file():
                raise ClientError(f"选择的 {self.name} 客户端文件不存在。")
            if path.suffix.lower() not in ({".exe", ".cmd"} if self.npm_shim else {".exe"}):
                raise ClientError(f"请选择 {self.name} 的客户端程序。")
            return str(path.resolve())
        if resolved := shutil.which(self.executable):
            return resolved
        candidates = []
        if self.npm_shim:
            roots = [Path(value) / "fnm" for key in ("LOCALAPPDATA", "APPDATA") if (value := os.getenv(key))]
            if fnm := os.getenv("FNM_DIR"):
                roots.insert(0, Path(fnm))
            shim = self.executable + ".cmd"
            candidates.extend(root / "aliases/default" / shim for root in roots)
            if roaming := os.getenv("APPDATA"):
                candidates.append(Path(roaming) / "npm" / shim)
            if local := os.getenv("LOCALAPPDATA"):
                candidates.extend(sorted((Path(local) / "Packages").glob(f"OpenAI.Codex_*/LocalCache/Roaming/npm/{shim}")))
        if local := os.getenv("LOCALAPPDATA"):
            for pattern in self.locations:
                candidates.extend(sorted(Path(local).glob(pattern), reverse=True))
        for path in candidates:
            if path.is_file():
                return str(path)
        raise ClientError(f"未找到 {self.name}。请安装并登录客户端，或在桌面选择客户端路径。")

    def check_client(self, run_dir: Path, explicit: str = "") -> str:
        client = self.find_client(explicit)
        try:
            executable, env = self.version_command(run_dir, client) if self.version_command else ([client], None)
            subprocess.run([*executable, "--version"], check=True, timeout=12, capture_output=True, env=env,
                           creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        except (OSError, ValueError, KeyError, subprocess.SubprocessError) as ex:
            raise ClientError(f"{self.name} 启动检查失败，请检查客户端和运行环境。") from ex
        return client


def _dsh_version(run_dir: Path, client: str) -> tuple[list[str], dict[str, str]]:
    command = DshBackend(run_dir, command=client)._command()
    return command, DshBackend._launch_env(command, dict(os.environ))


def _external(options: ProviderOptions) -> Provider:
    from .external_connection import ExternalSessionBackend

    return ExternalSessionBackend(options.directory(), progress=options.progress)


def _recover_dsh(commands: Path) -> None:
    from .usage_meter import recover_dsh_receipts

    recover_dsh_receipts(commands.parent, commands)


def _recover_agy(commands: Path) -> None:
    from .usage_meter import recover_agy_receipts

    recover_agy_receipts(commands.parent)


PROVIDERS = {spec.id: spec for spec in (
    ProviderSpec("pi", "Pi", "pi", lambda o: PiBackend(o.directory(), o.model, o.command,
        effort=o.effort, progress=o.progress, local_sessions_only=o.local_sessions_only),
        efforts=("default", "off", "minimal", "low", "medium", "high", "xhigh", "max"),
        desktop=True, npm_shim=True, version_command=lambda root, cmd: (PiBackend(root, command=cmd)._executable(), None),
        missing_usage="pi_response_usage_unavailable"),
    ProviderSpec("dsh", "DeepSeek (dsh)", "dsh", lambda o: DshBackend(o.directory(), o.model, o.command,
        effort=o.effort, progress=o.progress), "deepseek-flash", "low", ("default", "off", "low", "high", "max"),
        desktop=True, npm_shim=True, locations=("Programs/DeepSeek Harness/resources/runtime/cli/bin/dsh.cmd",),
        version_command=_dsh_version, usage_source="dsh_assistant_message", missing_usage="dsh_response_usage_unavailable",
        recover_usage=_recover_dsh),
    ProviderSpec("codex", "Codex", "codex.exe", lambda o: CodexBackend(o.directory(), o.model, o.command,
        effort=o.effort, progress=o.progress, http_provider=(o.settings or {}).get("httpProvider")),
        default_effort="low", efforts=("default", "low", "medium", "high", "xhigh"), desktop=True,
        locations=("OpenAI/Codex/bin/*/codex.exe",), usage_projection=codex_turn_usage, missing_usage="codex_usage_unavailable"),
    ProviderSpec("mcode", "MiniMax Code", "mcode", lambda o: McodeBackend(o.directory(), o.model, o.command,
        effort=o.effort, progress=o.progress), efforts=("default", "low", "medium", "high", "xhigh", "max"),
        desktop=True, npm_shim=True,
        version_command=lambda root, cmd: (McodeBackend(root, command=cmd)._launch_args(cmd, cancel_hook=False), None),
        usage_source="mcode_exec_result", missing_usage="mcode_exec_usage_unavailable"),
    ProviderSpec("kimi", "Kimi", "kimi.exe", lambda o: KimiBackend(o.model, o.command, cwd=o.cwd, progress=o.progress,
        agent=(o.settings or {}).get("agent"), agent_file=(o.settings or {}).get("agentFile")), "kimi-code/k3"),
    ProviderSpec("agy", "Antigravity", "agy.exe", lambda o: AgyBackend(o.legacy_execute), "gemini-3.8-flash", recover_usage=_recover_agy),
)}

EXTERNAL_PROVIDER = ProviderSpec("external:mcode", "MiniMax Code", "mcode", _external,
    usage_source="mcode_exec_result", missing_usage="mcode_exec_usage_unavailable")


def desktop_catalogue() -> list[dict[str, Any]]:
    return [{"id": spec.id, "name": spec.name, "defaultModel": spec.default_model, "allowCmd": spec.npm_shim,
             "defaultEffort": spec.default_effort, "efforts": list(spec.efforts)}
            for spec in PROVIDERS.values() if spec.desktop]


def cancel_provider(backend: Provider | None, process: Any) -> None:
    if process is None or process.poll() is not None:
        return
    terminate = getattr(backend, "terminate", None)
    if callable(terminate):
        terminate(process)
    else:
        process.kill()


if __name__ == "__main__":
    print(json.dumps(desktop_catalogue(), ensure_ascii=True))
