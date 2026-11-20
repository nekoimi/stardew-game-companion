from __future__ import annotations

import json
import logging
import os
import queue
import re
import subprocess
import tempfile
import threading
import time
import uuid
from pathlib import Path
from typing import Any

from stardew_ai_runtime.adapters.providers.processes import terminate_owned_process
from stardew_ai_runtime.application.provider_types import (
    ActiveTaskLike,
    BackendProgress,
    BackendResult,
    ProgressCallback,
)

logger = logging.getLogger(__name__)


class McodeBackend:
    """Use the local MiniMax Code CLI (``mcode exec``) bound to this game run.

    The game MCP server is attached for this invocation only: each turn writes
    ``.mcp.json`` into a workspace directory owned by this game run and points
    ``mcode exec --cwd`` at it.  A workspace entry shadows a same-named server
    from the player's shared profile, and its ``env`` carries this turn's
    authority, so the player's own MiniMax Code configuration is never read or
    rewritten.  The data directory stays untouched, which keeps the existing
    login and model selection in charge.
    """

    name = "mcode"
    @staticmethod
    def terminate(proc: Any) -> None:
        marker = getattr(proc, "stardew_cancel_file", None)
        if marker is not None and proc.poll() is None:
            try:
                Path(marker).touch()
                proc.wait(timeout=8)
            except (OSError, subprocess.TimeoutExpired):
                pass
        terminate_owned_process(proc)
    server_name = "stardew-companion"
    #: ``--effort`` values mcode accepts; "default" omits the flag entirely.
    efforts = frozenset({"default", "low", "medium", "high", "xhigh", "max"})
    #: Stable ``mcode exec`` exit codes, mapped to this project's error codes.
    exit_codes = {
        2: ("MCODE_REQUEST_INVALID", "mcode 拒绝了本次调用参数，请检查模型标识与推理档位是否被该模型支持。"),
        3: ("MCODE_CONFIG_INVALID", "mcode 配置无效，请检查本机 MiniMax Code 配置。"),
        4: ("MCODE_RUNTIME_FAILED", "mcode 本轮运行失败，请稍后重试。"),
        6: ("TIMEOUT", "mcode 响应超时，操作已中止。"),
        7: ("MCODE_LIMIT_EXCEEDED", "mcode 超出本轮运行限制，请稍后重试。"),
        70: ("MCODE_INTERNAL_ERROR", "mcode 内部错误，请稍后重试。"),
    }

    def __init__(self, run_dir: Path, model: str = "", command: str = "mcode",
                 progress: ProgressCallback | None = None, timeout_seconds: float = 600.0,
                 effort: str | None = "default"):
        self.run_dir = Path(run_dir)
        self.model = model
        self.command = command
        self.progress = progress
        self.timeout_seconds = timeout_seconds
        if effort not in (None, *self.efforts):
            raise ValueError("Unsupported mcode reasoning effort")
        self.effort = effort

    def _executable(self) -> str:
        """Resolve the CLI, preferring the shell-free shim over PowerShell."""
        import shutil
        executable = shutil.which(self.command)
        if executable is None:
            raise FileNotFoundError("Install MiniMax Code CLI (mcode) first")
        if Path(executable).suffix.lower() == ".ps1":
            # An execution policy can block the npm .ps1 shim; its .cmd sibling
            # runs the same entry point without a command shell.
            sibling = Path(executable).with_suffix(".cmd")
            if sibling.is_file():
                return str(sibling)
        return executable

    def compact_session(self, active_task: ActiveTaskLike, session_id: str) -> dict[str, Any]:
        from stardew_ai_runtime.adapters.providers.mcode_compaction import compact_existing_session
        result = compact_existing_session(self, active_task, session_id)
        if result.get("success") and result.get("compacted"):
            from stardew_ai_runtime.prompt_delivery import PromptDelivery
            delivery = PromptDelivery(self.run_dir / "prompt-delivery.json")
            for key in list(delivery.state):
                delivery.invalidate_session(key, session_id)
        return result

    def _write_workspace(self, surface: str) -> Path:
        """Write this turn's game-only MCP file and return its directory."""
        import sys
        python = self.run_dir / "runtime" / "python" / "python.exe"
        if not python.is_file():
            python = Path(sys.executable)
        context = {key: os.environ[key] for key in (
            "STARDEW_LIFE_MODE", "STARDEW_LIFE_TURN_ID", "STARDEW_LIFE_SAVE_ID",
            "STARDEW_LIFE_PROPOSAL_ID", "STARDEW_DECISION_TOKEN") if key in os.environ}
        context["STARDEW_MCP_SURFACE"] = surface
        context["STARDEW_MCP_WIRE_STYLE"] = "mcode"
        context.setdefault("PYTHONPATH", str(Path(__file__).resolve().parents[3]))
        workspace = self.run_dir / "data" / "mcode"
        workspace.mkdir(parents=True, exist_ok=True)
        document = {"mcpServers": {self.server_name: {
            "type": "stdio", "command": str(python),
            "args": ["-B", "-m", "stardew_ai_runtime.mcp_server", "--run-dir", str(self.run_dir),
                     "--surface", surface],
            "env": context, "cwd": str(self.run_dir),
        }}}
        path = workspace / ".mcp.json"
        temp = path.with_suffix(".tmp")
        temp.write_text(json.dumps(document, ensure_ascii=False, indent=2), encoding="utf-8")
        temp.replace(path)
        return workspace

    def _launch_args(self, executable: str, *, cancel_hook: bool = True) -> list[str]:
        if Path(executable).suffix.lower() in {".cmd", ".ps1"}:
            import shutil
            # npm shims invoke cmd.exe; run their JS entry directly so neither
            # game paths nor model arguments are interpreted by a shell.
            shim_dir = Path(executable).parent
            modules = shim_dir.parent if shim_dir.name.lower() == ".bin" else shim_dir / "node_modules"
            entry = modules / "@minimax-ai" / "code" / "cli.js"
            sibling = shim_dir / "node.exe"
            node = str(sibling) if sibling.is_file() else shutil.which("node")
            if not node or not entry.is_file():
                raise FileNotFoundError("Repair the MiniMax Code npm installation and Node.js")
            hook = ["--import", Path(__file__).with_name("mcode_cancel.mjs").resolve().as_uri()] if cancel_hook else []
            return [node, *hook, str(entry)]
        return [executable]

    def _command(self, executable: str, workspace: Path, session_id: str | None) -> list[str]:
        cmd = [*self._launch_args(executable), "exec", "--cwd", str(workspace),
               "--output-format", "stream-json", "--permission", "full",
               "--input", "-"]
        if self.model:
            cmd.extend(["--model", self.model])
        cmd.extend(["--effort", self.effort if self.effort not in (None, "default") else "low"])
        if session_id:
            cmd.extend(["--session", session_id])
        return cmd

    @staticmethod
    def _usage(raw: Any) -> dict[str, Any] | None:
        """Normalize one exec result's usage into the bridge's shared shape."""
        if not isinstance(raw, dict):
            return None

        def number(key: str) -> int | None:
            value = raw.get(key)
            return value if isinstance(value, int) and not isinstance(value, bool) and value >= 0 else None

        input_tokens = number("inputTokens")
        output_tokens = number("outputTokens")
        cache_read = number("cacheReadTokens")
        total = number("totalTokens")
        if all(value is None for value in (input_tokens, output_tokens, cache_read, total)):
            return None
        # mcode reports input and cache-read separately; the shared shape counts
        # cache inside the input total, as the usage display expects.
        # An exec can contain multiple model requests. These totals are useful
        # for accounting but cannot measure the latest request's context size.
        # The CLI exposes exec totals, not a request count. A multi-request exec
        # must not be displayed as one model call merely because it has one result.
        usage: dict[str, Any] = {"input_includes_cache": True,
                                 "input_context_measured": False, "source": "mcode_exec_result"}
        if input_tokens is not None and cache_read is not None:
            usage["input_tokens"] = input_tokens + cache_read
        if output_tokens is not None:
            usage["output_tokens"] = output_tokens
        if cache_read is not None:
            usage["cache_read_tokens"] = cache_read
        if total is not None and cache_read is not None:
            usage["total_tokens"] = total + cache_read
        elif "input_tokens" in usage and output_tokens is not None:
            usage["total_tokens"] = usage["input_tokens"] + output_tokens
        if any(field not in usage for field in ("total_tokens", "input_tokens", "output_tokens", "cache_read_tokens")):
            usage["partial"] = True
        return usage

    @staticmethod
    def _latest_request_context(session_id: str | None, not_before_unix: float) -> int | None:
        """Read only this invocation's latest request counter, never exec totals.

        The local CLI's numeric counters are optional evidence. A missing or
        changed SQLite schema stays unknown; no billing total is substituted.
        """
        if not session_id:
            return None
        import sqlite3
        from contextlib import closing
        root = Path(os.environ.get("MINIMAX_DATA_DIR") or Path.home() / ".minimax")
        path = root / "v2/sqlite/runtime-state.sqlite"
        if not path.is_file():
            return None
        try:
            with closing(sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True, timeout=0.2)) as connection:
                row = connection.execute(
                    "SELECT ts, input_tokens, cache_read_tokens, cache_write_tokens "
                    "FROM local_runtime_token_usage WHERE session_id=? ORDER BY id DESC LIMIT 1",
                    (session_id,),
                ).fetchone()
            if row is None or not isinstance(row[0], (int, float)):
                return None
            timestamp = row[0] / 1000 if row[0] > 100_000_000_000 else row[0]
            if timestamp < not_before_unix:
                return None
            values = row[1:]
            if any(type(value) is not int or value < 0 for value in values):
                return None
            return sum(values)
        except (OSError, sqlite3.Error, ValueError):
            return None

    @staticmethod
    def _tool_label(name: str | None) -> str | None:
        """Drop the mcp__<server>__ prefix so replies show the real tool."""
        if not name:
            return None
        stripped = re.sub(r"^mcp__[A-Za-z0-9.-]+__", "", name)
        return stripped or name

    def _invoke(self, active_task: ActiveTaskLike, session_id: str | None, prompt: str,
                executable: str, workspace: Path, start: float) -> dict[str, Any]:
        cmd = self._command(executable, workspace, session_id)
        events: queue.Queue[tuple[str, str | None]] = queue.Queue()
        proc: Any = None
        requested_session = session_id
        started = False
        active_task.provider_input_delivered = False
        visible_messages: dict[str, str] = {}
        invocation_unix = time.time()
        terminal_error: Any = None
        # A file-backed stdin avoids both Windows command-line limits and a
        # blocking pipe write before the cancellation/deadline loop can start.
        prompt_stream = tempfile.TemporaryFile()
        cancel_file = Path(tempfile.gettempdir()) / ("stardew-mcode-cancel-" + uuid.uuid4().hex)
        process_env = dict(os.environ, STARDEW_MCODE_CANCEL_FILE=str(cancel_file))
        try:
            prompt_stream.write(prompt.encode("utf-8"))
            prompt_stream.seek(0)
            proc = subprocess.Popen(cmd, cwd=str(workspace), stdin=prompt_stream, stdout=subprocess.PIPE,
                                    stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace", env=process_env)
            proc.stardew_cancel_file = cancel_file
            active_task.process = proc
            if self.progress:
                self.progress(BackendProgress(self.name, "started", "已启动 MiniMax Code"))

            def read(stream: Any, channel: str) -> None:
                try:
                    for line in stream:
                        events.put((channel, line))
                finally:
                    events.put((channel, None))

            readers = [threading.Thread(target=read, args=(stream, channel), daemon=True)
                       for stream, channel in ((proc.stdout, "stdout"), (proc.stderr, "stderr"))]
            for reader in readers:
                reader.start()
            done: set[str] = set()
            reply = ""
            usage = None
            status = ""
            errors: list[str] = []
            deadline = start + self.timeout_seconds
            while len(done) < 2 and time.monotonic() < deadline:
                if active_task.cancelled:
                    self.terminate(proc)
                    if usage:
                        usage["partial"] = True
                    return BackendResult(False, "任务已取消。", session_id, usage, provider=self.name,
                                         duration=time.monotonic() - start).as_dict()
                try:
                    channel, line = events.get(timeout=0.1)
                except queue.Empty:
                    continue
                if line is None:
                    done.add(channel)
                    continue
                if channel == "stderr":
                    errors.append(line[:500])
                    continue
                try:
                    event = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if not isinstance(event, dict):
                    continue
                kind = event.get("type")
                item_type = (event.get("item") or {}).get("type")
                if kind in {"item.started", "item.completed"} and item_type in {"tool_call", "agent_message"}:
                    active_task.provider_input_delivered = True
                # A productive sequence has no aggregate wall-clock limit. Bound
                # each silent interval instead; native actions retain their own watchdog.
                if kind in {"item.started", "item.completed"} and item_type == "tool_call":
                    deadline = time.monotonic() + self.timeout_seconds
                # Public provider events only: never persist reasoning items or credentials.
                public_item = event.get("item") or {}
                if (kind in {"session.started", "session.resumed", "turn.completed", "turn.failed", "exec.completed"}
                        or kind == "item.completed" and public_item.get("type") in {"tool_call", "agent_message"}):
                    def scrub(value):
                        if isinstance(value, dict):
                            return {key: ("[redacted]" if key.lower().replace("_", "") in
                                    {"sessiontoken", "decisiontoken", "accesstoken", "refreshtoken", "authorization", "apikey", "secret"} else scrub(item))
                                    for key, item in value.items()}
                        if isinstance(value, list):
                            return [scrub(item) for item in value]
                        return value
                    try:
                        trace_path = self.run_dir / "data" / "mcode-public-events.jsonl"
                        with trace_path.open("a", encoding="utf-8") as trace:
                            trace.write(json.dumps({"capturedAt": time.time(),
                                "requestId": getattr(active_task, "request_id", ""),
                                "event": scrub(event)}, ensure_ascii=False) + "\n")
                    except OSError:
                        logger.warning("Could not record public mcode event")
                if kind in {"session.started", "session.resumed", "turn.started", "item.started", "item.completed", "turn.completed"}:
                    started = True
                if kind in {"session.started", "session.resumed"}:
                    session_id = event.get("sessionId") or session_id
                    if session_id and self.progress:
                        self.progress(BackendProgress(self.name, "session_ready", session_id))
                elif kind == "turn.completed":
                    usage = self._usage(event.get("usage")) or usage
                elif kind == "item.started":
                    item = event.get("item") or {}
                    if self.progress and item.get("type") == "tool_call":
                        self.progress(BackendProgress(self.name, "tool_started", "正在使用工具",
                                                      self._tool_label((item.get("toolCall") or {}).get("name"))))
                elif kind == "turn.failed":
                    terminal_error = event.get("error")
                    errors.append(str(event.get("error") or "mcode turn failed"))
                elif kind == "item.completed":
                    item = event.get("item") or {}
                    if item.get("type") == "agent_message" and item.get("content"):
                        text = str(item["content"])
                        message_id = str(item.get("id") or text)
                        if message_id not in visible_messages and self.progress:
                            self.progress(BackendProgress(self.name, "public_message", text))
                        visible_messages[message_id] = text
                        reply = text
                    elif item.get("type") == "tool_call":
                        call = item.get("toolCall") or {}
                        if self.progress:
                            self.progress(BackendProgress(self.name, "tool_completed", "正在处理游戏任务",
                                                          self._tool_label(call.get("name"))))
                elif kind == "exec.completed":
                    result = event.get("result") or {}
                    session_id = result.get("sessionId") or session_id
                    status = str(result.get("status") or "")
                    if result.get("error"):
                        terminal_error = result["error"]
                        errors.append(str(result["error"]))
                    if result.get("output"):
                        output = str(result["output"])
                        if output != reply and output not in visible_messages.values():
                            reply = output
                    usage = self._usage(result.get("usage")) or usage
                    if usage and result.get("usageIncomplete"):
                        usage["partial"] = True
            if len(done) < 2:
                self.terminate(proc)
                if usage:
                    usage["partial"] = True
                return BackendResult(False, "MiniMax Code 执行超时，操作已中止。", session_id, usage,
                                     error={"code": "TIMEOUT", "message": "mcode process deadline exceeded"},
                                     provider=self.name, duration=time.monotonic() - start).as_dict()
            proc.wait(timeout=2)
            if usage is not None:
                context = self._latest_request_context(session_id, invocation_unix)
                if context is not None:
                    usage["latestRequestInputContext"] = context
                    usage["latest_request_context_source"] = "mcode_local_request_counter"
            failure_message = (terminal_error.get("message", "") if isinstance(terminal_error, dict)
                               else str(terminal_error or ""))
            if isinstance(terminal_error, dict) and terminal_error.get("code") == "CONTENT_REVIEW_BLOCKED":
                message = "MiniMax Code 的内容审核拒绝了本次请求。已保留游戏实际进度，本轮未获得模型回复。"
                return BackendResult(False, message, session_id, usage,
                                     {"code": "CONTENT_REVIEW_BLOCKED", "message": message},
                                     self.name, time.monotonic() - start).as_dict()
            if (re.search(r"\b2067\b", failure_message)
                    and re.search(r"Token Plan|用量上限", failure_message, re.IGNORECASE)):
                return BackendResult(False, "MiniMax Code 的 Token Plan 用量已耗尽；已停止新请求，保留实际进度。",
                                     session_id, usage, {"code": "RESOURCE_EXHAUSTED", "message": failure_message},
                                     self.name, time.monotonic() - start).as_dict()
            if proc.returncode == 0 and status == "succeeded" and reply:
                if self.progress:
                    self.progress(BackendProgress(self.name, "completed", "MiniMax Code 已完成回复"))
                return BackendResult(True, reply, session_id, usage, provider=self.name,
                                     duration=time.monotonic() - start).as_dict()
            detail = "".join(errors)[-500:]
            if "defaultmodel is not configured" in detail.lower():
                # A replacement --config can omit the player's selected model.
                # Repeating the same exec cannot repair this configuration.
                message = "MiniMax Code 未配置默认模型。请先选择模型；使用临时 --config 时也需保留 defaultModel。"
                return BackendResult(False, message, session_id, usage,
                                     {"code": "MCODE_MODEL_NOT_CONFIGURED", "message": message},
                                     self.name, time.monotonic() - start).as_dict()
            if (requested_session and not started and usage is None
                    and re.search(r"session (?:not found|is archived|workspace does not match)", detail, re.IGNORECASE)):
                # Preserve identity on failure; only the player may replace it.
                return BackendResult(False, "mcode 无法恢复原对话，请修复连接；未创建替代会话。", requested_session, usage,
                                     {"code": "MCODE_SESSION_UNAVAILABLE", "message": detail},
                                     self.name, time.monotonic() - start).as_dict()
            if proc.returncode in self.exit_codes:
                code, message = self.exit_codes[proc.returncode]
                return BackendResult(False, message, session_id, usage, {"code": code, "message": detail},
                                     self.name, time.monotonic() - start).as_dict()
            return BackendResult(False, "MiniMax Code 请求失败，请稍后重试。", session_id, usage,
                                 {"code": "MCODE_REQUEST_FAILED", "message": detail}, self.name,
                                 time.monotonic() - start).as_dict()
        finally:
            prompt_stream.close()
            if proc is not None:
                try:
                    if proc.poll() is None:
                        self.terminate(proc)
                except Exception:
                    logger.exception("Failed to clean up mcode process")
            cancel_file.unlink(missing_ok=True)
            active_task.process = None

    def run(self, active_task: ActiveTaskLike, session_id: str | None, prompt: str) -> dict[str, Any]:
        start = time.monotonic()
        try:
            executable = self._executable()
        except FileNotFoundError as ex:
            return BackendResult(False, "没找到 mcode，请先安装 MiniMax Code CLI。", session_id,
                                 error={"code": "MCODE_CLI_MISSING", "message": str(ex)},
                                 provider=self.name, duration=time.monotonic() - start).as_dict()
        surface = "life" if os.environ.get("STARDEW_MCP_SURFACE") == "life" else "light"
        try:
            workspace = self._write_workspace(surface)
        except OSError as ex:
            logger.warning("Could not prepare the mcode game tool profile")
            return BackendResult(False, "MCode 游戏工具配置失败，请检查 Mod 运行目录。", session_id,
                                 error={"code": "MCODE_GAME_PROFILE_FAILED", "message": str(ex)},
                                 provider=self.name, duration=time.monotonic() - start).as_dict()
        try:
            return self._invoke(active_task, session_id, prompt, executable, workspace, start)
        except OSError as ex:
            return BackendResult(False, "MiniMax Code 启动失败，请检查 CLI 和 Node.js 安装。", session_id,
                                 error={"code": "MCODE_START_FAILED", "message": str(ex)},
                                 provider=self.name, duration=time.monotonic() - start).as_dict()
