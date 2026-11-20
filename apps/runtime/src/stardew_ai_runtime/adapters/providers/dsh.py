from __future__ import annotations

import json
import logging
import os
import queue
import subprocess
import threading
import time
import uuid
from pathlib import Path
from typing import Any

from stardew_ai_runtime.adapters.providers.processes import terminate_owned_process
from stardew_ai_runtime.agent_instructions import runtime_instructions
from stardew_ai_runtime.application.provider_types import (
    ActiveTaskLike,
    BackendProgress,
    BackendResult,
    ProgressCallback,
)

logger = logging.getLogger(__name__)


class DshBackend:
    """Official dsh SDK JSON-RPC adapter, with a reusable chat-only runtime.

    Uses the installed SDK profile protocol directly, so packaged Python needs
    no second dsh installation. Work runtimes close at handoff to release their
    native observation socket; durable provider sessions still resume.
    """

    name = "dsh"
    terminate = staticmethod(terminate_owned_process)

    def __init__(self, run_dir: Path, model: str = "deepseek-flash", command: str = "dsh",
                 progress: ProgressCallback | None = None, timeout_seconds: float = 180,
                 effort: str | None = "low"):
        self.run_dir = Path(run_dir)
        self.model = model or "deepseek-flash"
        self.command = command
        self.progress = progress
        self.timeout_seconds = timeout_seconds
        self.effort = effort if effort not in {None, "default"} else "low"
        if self.effort not in {"off", "low", "high", "max"}:
            raise ValueError("Unsupported dsh reasoning effort")
        self._process: Any = None
        self._events: queue.Queue = queue.Queue()
        self._surface: str | None = None
        self._rpc_id = 0
        self._diagnostic_state = {"code": "DSH_RUNTIME_FAILED"}

    def _command(self) -> list[str]:
        import shutil
        executable = shutil.which(self.command)
        if executable is None:
            raise FileNotFoundError("Install DeepSeek Harness (dsh) first")
        if Path(executable).suffix.lower() in {".cmd", ".ps1"}:
            # npm shims are not native executables. Use their declared JS entry,
            # never pass prompt text or credentials through a command shell.
            shim = Path(executable)
            package = shim.parent / "node_modules" / "@deepseek-ai" / "dsh"
            if not (package / "package.json").is_file() and tuple(part.casefold() for part in shim.parts[-5:]) == (
                    "resources", "runtime", "cli", "bin", "dsh.cmd"):
                host = shim.parents[4] / "DeepSeek Harness.exe"
                archive = shim.parents[3] / "app.asar"
                if host.is_file() and archive.is_file():
                    entry = archive / "dsh/node_modules/@deepseek-ai/dsh-desktop-host/lib/cli.js"
                    return [str(host), "--expose-internals", str(entry)]
            manifest = json.loads((package / "package.json").read_text(encoding="utf-8"))
            sibling = shim.parent / "node.exe"
            node = str(sibling) if sibling.is_file() else shutil.which("node")
            if not node:
                raise FileNotFoundError("dsh npm installation requires Node.js")
            return [node, str(package / manifest["bin"]["dsh"])]
        return [executable]

    @staticmethod
    def _launch_env(command: list[str], environment: dict[str, str]) -> dict[str, str]:
        return {**environment, "ELECTRON_RUN_AS_NODE": "1"} if "--expose-internals" in command else environment

    def close(self) -> None:
        proc, self._process = self._process, None
        self._surface = None
        if proc is not None:
            self.terminate(proc)

    def _send(self, method: str, params: dict | None = None) -> int:
        self._rpc_id += 1
        frame = {"jsonrpc": "2.0", "id": self._rpc_id, "method": method}
        if params is not None:
            frame["params"] = params
        self._process.stdin.write(json.dumps(frame, ensure_ascii=False) + "\n")
        self._process.stdin.flush()
        return self._rpc_id

    def _prepare(self, surface: str) -> tuple[Path, dict[str, str]]:
        import sys
        context = {key: os.environ[key] for key in (
            "STARDEW_MCP_SURFACE", "STARDEW_LIFE_MODE", "STARDEW_LIFE_TURN_ID",
            "STARDEW_LIFE_SAVE_ID", "STARDEW_DECISION_TOKEN") if key in os.environ}
        context_path = self.run_dir / "data" / ("dsh-turn-" + surface + ".json")
        context_path.parent.mkdir(parents=True, exist_ok=True)
        temp = context_path.with_suffix(".tmp")
        temp.write_text(json.dumps(context), encoding="utf-8")
        temp.replace(context_path)
        python = self.run_dir / "runtime" / "python" / "python.exe"
        python = python if python.is_file() else Path(sys.executable)
        env = dict(os.environ)
        if not env.get("DEEPSEEK_API_KEY"):
            # Read only the official route's reference, never copy the credentials
            # file, mutate dsh settings, or log parser messages containing secrets.
            import yaml
            credential_home = Path(os.environ.get("DSH_HOME") or Path.home() / ".dsh")
            credential_file = credential_home / ".credentials.yaml"
            try:
                credentials = yaml.safe_load(credential_file.read_text(encoding="utf-8")) or {}
                refs = credentials.get("refs", {}) if credentials.get("version") == 1 else credentials
                key = refs.get("DEEPSEEK_API_KEY")
                if isinstance(key, str) and key.strip():
                    env["DEEPSEEK_API_KEY"] = key
            except FileNotFoundError:
                pass
            except Exception:
                raise RuntimeError("DSH_CREDENTIAL_STORE_UNREADABLE") from None
        if not env.get("DEEPSEEK_API_KEY"):
            raise RuntimeError("DSH_MISSING_CREDENTIAL")
        # Separate homes prevent inherited coding tools/configuration from
        # entering this game-only profile. Credentials remain environment-owned.
        env["DSH_HOME"] = str(self.run_dir / "data" / "dsh-home")
        env["DSH_SYSTEM_PROMPT"] = runtime_instructions()
        # Keep this shim beside the profile's official node_modules so Node can
        # resolve its dependencies. Never patch the user's global installation.
        resume_plugin = Path(env["DSH_HOME"]) / "profiles" / "stardew-sdk-resume.mjs"
        resume_plugin.parent.mkdir(parents=True, exist_ok=True)
        resume_plugin.write_bytes(Path(__file__).with_name("dsh_resume_plugin.mjs").read_bytes())
        patch = [
            {"id": "sdk-jsonrpc-server", "disabled": True},
            {"insert": [{"id": "stardew-sdk-resume", "name": resume_plugin.as_uri()}]},
            {"id": "session-log-deepseek", "disabled": True},
            {"insert": [{"id": "stardew-mcp", "name": "@deepseek-ai/dsh-mcp-client", "config": {
                "transport": "stdio", "serverName": "stardew-companion", "command": str(python),
                "args": ["-B", "-m", "stardew_ai_runtime.mcp_server", "--run-dir", str(self.run_dir), "--surface", surface],
                "env": {"STARDEW_TURN_CONTEXT": str(context_path), "STARDEW_MCP_SURFACE": surface,
                        "PYTHONPATH": os.environ.get("PYTHONPATH", str(Path(__file__).resolve().parents[3]))},
                "cwd": str(self.run_dir), "toolCallTimeoutMs": 120000, "failOnStartupError": True,
            }}]},
        ]
        patch_path = self.run_dir / "data" / ("dsh-" + surface + ".patch.json")
        patch_path.write_text(json.dumps(patch, ensure_ascii=False, indent=2), encoding="utf-8")
        return patch_path, env

    def _next(self, active_task: ActiveTaskLike, deadline: float) -> dict[str, Any]:
        while time.monotonic() < deadline:
            if active_task.cancelled:
                raise InterruptedError("cancelled")
            try:
                event = self._events.get(timeout=0.1)
            except queue.Empty:
                if self._process is None or self._process.poll() is not None:
                    raise RuntimeError("dsh runtime exited") from None
                continue
            if event is None:
                raise RuntimeError("dsh runtime closed its protocol stream")
            return event
        raise TimeoutError("dsh request timed out")

    def _start(self, active_task: ActiveTaskLike, surface: str, patch: Path, env: dict[str, str]) -> None:
        if self._process is not None and self._process.poll() is None and self._surface == surface:
            active_task.process = self._process
            return
        self.close()
        self._events = queue.Queue()
        # Each process owns its diagnostics. A late daemon reader only updates
        # its captured state, even if this backend has already restarted.
        diagnostic_state = self._diagnostic_state = {"code": "DSH_RUNTIME_FAILED"}
        command = self._command()
        self._process = subprocess.Popen(
            [*command, "--profile", "sdk-minimal", "--patch", str(patch)],
            cwd=str(self.run_dir), env=self._launch_env(command, env), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace",
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        proc = self._process
        active_task.process = proc
        events = self._events

        def read() -> None:
            try:
                for line in proc.stdout:
                    try:
                        item = json.loads(line)
                        if isinstance(item, dict):
                            events.put(item)
                    except ValueError:
                        continue
            finally:
                events.put(None)

        def drain_errors() -> None:
            # Runtime diagnostics can contain account paths; no raw stderr in UI.
            for line in proc.stderr:
                # Keep only fixed categories. Never retain a credential-bearing
                # line, complete provider response or account path.
                if "MISSING_CREDENTIAL" in line or "no API key" in line:
                    diagnostic_state["code"] = "DSH_MISSING_CREDENTIAL"
                elif "401" in line or "authentication" in line.lower():
                    diagnostic_state["code"] = "DSH_AUTH_FAILED"
                elif "Cannot find package" in line or "plugin" in line.lower() and "not found" in line.lower():
                    diagnostic_state["code"] = "DSH_PROFILE_UNAVAILABLE"
                elif "DSH_RESUME_PROTOCOL_UNSUPPORTED" in line:
                    diagnostic_state["code"] = "DSH_RESUME_PROTOCOL_UNSUPPORTED"

        threading.Thread(target=read, daemon=True).start()
        threading.Thread(target=drain_errors, daemon=True).start()
        request_id = self._send("initialize", {"cwd": str(self.run_dir), "provider": "deepseek-official",
            "model": self.model, "reasoningEffort": self.effort, "maxTokens": 8192})
        deadline = time.monotonic() + min(45, self.timeout_seconds)
        while True:
            frame = self._next(active_task, deadline)
            if frame.get("id") == request_id:
                if frame.get("error"):
                    raise RuntimeError("DSH_MODEL_UNAVAILABLE")
                info = (frame.get("result") or {}).get("serverInfo") or {}
                if info.get("name") != "deepseek-harness-sdk-runtime" or info.get("stardewSessionResume") != 1:
                    raise RuntimeError("DSH_PROTOCOL_MISMATCH")
                break
        self._surface = surface

    def run(self, active_task: ActiveTaskLike, session_id: str | None, prompt: str) -> dict[str, Any]:
        start = time.monotonic()
        session_id = session_id or "stardew-" + uuid.uuid4().hex
        surface = "life" if os.environ.get("STARDEW_MCP_SURFACE") == "life" else "light"
        usage: dict[str, Any] = {}
        seen_responses: set[str] = set()
        turn_nonce = uuid.uuid4().hex
        reply = ""
        try:
            patch, env = self._prepare(surface)
            self._start(active_task, surface, patch, env)
            if self.progress:
                self.progress(BackendProgress(self.name, "session_ready", session_id))
                self.progress(BackendProgress(self.name, "started", "正在想下一步"))
            request_id = self._send("session/prompt", {"sessionId": session_id,
                "contentBlocks": [{"type": "text", "text": prompt}]})
            deadline = time.monotonic() + self.timeout_seconds
            finish = None
            failure_code = "DSH_TURN_FAILED"
            running = False
            receipt = False
            idle_seen = False
            turn_started = False
            active_turn = None
            while True:
                if receipt and idle_seen and finish is not None:
                    break
                frame = self._next(active_task, deadline)
                if frame.get("id") == request_id:
                    if frame.get("error"):
                        error = str((frame.get("error") or {}).get("message", "")).lower()
                        code = ("DSH_SESSION_OWNED" if "owned" in error or "locked" in error
                                else "DSH_SESSION_EXISTS" if "already exists" in error
                                else "DSH_SESSION_CORRUPT" if "corrupt" in error or "format" in error
                                else "DSH_REQUEST_REJECTED")
                        raise RuntimeError(code)
                    receipt = True
                    continue
                params = frame.get("params") or {}
                if params.get("sessionId") != session_id:
                    continue  # Child/session output never replaces root output.
                if frame.get("method") == "session.status":
                    if params.get("status") == "running":
                        running = True
                    if params.get("status") == "idle" and (running or finish is not None):
                        idle_seen = True
                elif frame.get("method") == "session.event":
                    event = params.get("event") or {}
                    data = event.get("data") or {}
                    if event.get("type") == "turn/start":
                        turn_started = True
                        active_turn = data.get("turn")
                        idle_seen = False
                        finish = None
                    if not turn_started:
                        continue
                    if active_turn is not None and data.get("turn") not in (None, active_turn):
                        continue
                    if event.get("type") == "assistant/message":
                        message = data.get("message") or {}
                        content = message.get("content") or []
                        text = "".join(str(block.get("text", "")) for block in content if block.get("type") == "text")
                        if text:
                            reply = text
                            if self.progress:
                                self.progress(BackendProgress(self.name, "response", text[:180]))
                        from stardew_ai_runtime.usage_meter import (
                            append_response_receipt,
                            dsh_response_usage,
                        )
                        measured = dsh_response_usage(data.get("usage") or {})
                        identity = message.get("id") or event.get("seq")
                        response_id = f"{session_id}:{identity}" if identity is not None else f"{session_id}:{turn_nonce}:{len(seen_responses)}"
                        if measured and response_id not in seen_responses:
                            seen_responses.add(response_id)
                            append_response_receipt(self.run_dir, response_id=response_id,
                                request_id=getattr(active_task, "request_id", ""),
                                save_id=getattr(active_task, "save_id", ""), conversation_id=session_id,
                                model=self.model, usage=measured,
                                channel="autonomy" if getattr(active_task, "request_id", "").startswith("autonomy-") else "conversation")
                            for key in ("input_tokens", "uncached_input_tokens", "output_tokens", "total_tokens", "cache_read_tokens", "thinking_tokens"):
                                if key in measured:
                                    usage[key] = usage.get(key, 0) + measured[key]
                            usage.update(source="dsh_assistant_message", input_includes_cache=True,
                                         generations_count=len(seen_responses))
                            usage["latestRequestInputContext"] = measured.get("latestRequestInputContext")
                            if measured.get("partial"):
                                usage["partial"] = True
                    elif event.get("type") == "tool/result" and self.progress:
                        self.progress(BackendProgress(self.name, "tool", "正在处理这项安排"))
                    elif event.get("type") == "turn/end":
                        reason = data.get("reason") or {}
                        finish = reason.get("kind")
                        # Map only known categories; provider failure objects can
                        # include request details and must never be echoed.
                        failure = json.dumps(reason.get("failure") or {}).upper()
                        if "TRANSPORT" in failure:
                            failure_code = "DSH_TRANSPORT_FAILED"
                        elif "AUTHENTICATION" in failure or "UNAUTHORIZED" in failure:
                            failure_code = "DSH_AUTH_FAILED"
                        if not isinstance(finish, str):
                            raise RuntimeError("Invalid dsh turn/end protocol")
            success = finish == "completed"
            if not success and usage:
                usage["partial"] = True
            return BackendResult(success, reply, session_id, usage or None,
                error=None if success else {"code": failure_code, "message":
                    "DeepSeek 网络连接失败，请稍后重试。" if failure_code == "DSH_TRANSPORT_FAILED"
                    else "DeepSeek 登录凭据未通过。" if failure_code == "DSH_AUTH_FAILED"
                    else "DeepSeek 未完成本次回复，请重试。"},
                provider=self.name, duration=time.monotonic() - start).as_dict()
        except Exception as ex:
            self.close()
            if usage:
                usage["partial"] = True
            known = {"DSH_MISSING_CREDENTIAL", "DSH_CREDENTIAL_STORE_UNREADABLE", "DSH_MODEL_UNAVAILABLE", "DSH_PROTOCOL_MISMATCH",
                     "DSH_SESSION_OWNED", "DSH_SESSION_EXISTS", "DSH_SESSION_CORRUPT", "DSH_REQUEST_REJECTED"}
            code = ("CANCELLED" if isinstance(ex, InterruptedError) else "TIMEOUT" if isinstance(ex, TimeoutError)
                    else "DSH_CLI_MISSING" if isinstance(ex, FileNotFoundError) else str(ex) if str(ex) in known
                    else self._diagnostic_state["code"])
            messages = {
                "CANCELLED": "已停下。", "TIMEOUT": "DeepSeek 响应超时，请稍后再试。",
                "DSH_CLI_MISSING": "没找到 dsh，请先安装 DeepSeek Harness。",
                "DSH_MISSING_CREDENTIAL": "请先在 dsh 保存 DeepSeek 凭据，或设置 DEEPSEEK_API_KEY。",
                "DSH_CREDENTIAL_STORE_UNREADABLE": "无法读取本机 dsh 凭据，请在 dsh 中检查登录设置。",
                "DSH_AUTH_FAILED": "DeepSeek 登录凭据未通过，请在 dsh 中更新。",
                "DSH_MODEL_UNAVAILABLE": "dsh 不支持当前模型或推理设置，请检查 deepseek-flash 与 low 是否可用。",
                "DSH_PROTOCOL_MISMATCH": "dsh SDK 协议不兼容，请检查安装版本。",
                "DSH_PROFILE_UNAVAILABLE": "dsh 缺少 SDK 或 MCP 组件，请修复 DeepSeek Harness 安装。",
                "DSH_RESUME_PROTOCOL_UNSUPPORTED": "当前 dsh SDK 版本尚不支持安全恢复会话，请使用支持的版本。",
                "DSH_SESSION_OWNED": "这段对话仍被另一个 dsh 进程占用，请结束重复服务后继续。",
                "DSH_SESSION_EXISTS": "dsh 未能恢复已有对话，记录已保留，请检查版本兼容性。",
                "DSH_SESSION_CORRUPT": "dsh 对话记录无法读取，原记录已保留。",
                "DSH_REQUEST_REJECTED": "dsh 拒绝了本次对话请求，请检查服务日志。",
            }
            return BackendResult(False, messages.get(code, "dsh 运行中断，请检查 DeepSeek Harness 安装后重试。"),
                session_id, usage or None, error={"code": code, "message": messages.get(code, "dsh runtime failed")},
                provider=self.name, duration=time.monotonic() - start).as_dict()
        finally:
            if surface != "life":
                self.close()
            active_task.process = None
