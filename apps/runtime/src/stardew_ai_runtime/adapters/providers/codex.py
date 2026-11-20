from __future__ import annotations

import json
import logging
import os
import queue
import re
import subprocess
import threading
import time
from pathlib import Path
from typing import Any

from stardew_ai_runtime.adapters.providers.codex_game_profile import (
    game_tool_config,
    unrelated_mcp_config,
    write_game_instructions,
)
from stardew_ai_runtime.adapters.providers.codex_usage import CodexUsageMeter
from stardew_ai_runtime.adapters.providers.processes import terminate_owned_process
from stardew_ai_runtime.agent_instructions import runtime_instructions
from stardew_ai_runtime.application.provider_types import (
    ActiveTaskLike,
    BackendProgress,
    BackendResult,
    ProgressCallback,
)
from stardew_ai_runtime.provider_diagnostics import record_completed, record_prepared, safe_error

logger = logging.getLogger(__name__)


class CodexBackend:
    """Use the locally authenticated Codex CLI and bind MCP to this game run."""

    name = "codex"

    @staticmethod
    def _approval_denial(item: dict[str, Any]) -> str | None:
        """Recognize a CLI tool error, never infer denial from agent prose."""
        error = item.get("error")
        result = item.get("result")
        if not error and not (isinstance(result, dict) and result.get("isError")) and item.get("status") != "failed":
            return None
        values = [error]
        if isinstance(result, dict) and (result.get("isError") or item.get("status") == "failed"):
            values.extend(result.get("content") or [])
        for value in values:
            message = value.get("message", value.get("text", "")) if isinstance(value, dict) else value
            if isinstance(message, str) and "MCP tool call requires approval, but approval policy is never" in message:
                return message
        return None

    terminate = staticmethod(terminate_owned_process)

    def __init__(self, run_dir: Path, model: str = "", command: str = "codex.exe",
                 progress: ProgressCallback | None = None, timeout_seconds: float = 600.0,
                 effort: str | None = None, http_provider: str | None = None):
        self.run_dir = Path(run_dir)
        self.model = model
        self.command = command
        self.progress = progress
        self.timeout_seconds = timeout_seconds
        if effort not in {None, "default", "low", "medium", "high", "xhigh"}:
            raise ValueError("Unsupported Codex reasoning effort")
        self.effort = effort
        if http_provider is not None and not re.fullmatch(r"[A-Za-z0-9_-]+", http_provider):
            raise ValueError("Invalid Codex HTTP provider identifier")
        self.http_provider = http_provider

    def run(self, active_task: ActiveTaskLike, session_id: str | None, prompt: str) -> dict[str, Any]:
        meter = CodexUsageMeter(session_id)
        result = self._run(active_task, session_id, prompt)
        result.update(meter.finish(result.get("conversation_id"), result.get("usage")))
        record_completed(self.run_dir, active_task.request_id, result)
        return result

    def _run(self, active_task: ActiveTaskLike, session_id: str | None, prompt: str) -> dict[str, Any]:
        start = time.monotonic()
        python = self.run_dir / "runtime" / "python" / "python.exe"
        if not python.is_file():
            return BackendResult(False, "游戏运行环境不完整。", session_id,
                                 error={"code": "BACKEND_START_FAILED", "message": "bundled Python missing"},
                                 provider=self.name).as_dict()
        try:
            inherited_mcp = unrelated_mcp_config(self.command, self.run_dir)
            instructions = getattr(active_task, "instructions_text", None)
            if not isinstance(instructions, str) or not instructions:
                instructions = runtime_instructions()
            instructions_file = write_game_instructions(self.run_dir, instructions)
            prompt = prompt.replace(instructions, "").strip()
        except Exception:
            # Do not start a model with an unverified inherited tool surface.
            # CLI configuration can include secrets, so keep errors generic.
            logger.warning("Could not prepare the Codex game tool profile")
            return BackendResult(False, "GPT 游戏工具配置失败，请检查本机 Codex 配置。", session_id,
                                 error={"code": "CODEX_GAME_PROFILE_FAILED",
                                        "message": "Could not prepare run-local Codex game profile"},
                                 provider=self.name).as_dict()
        # CLI -c overrides only this invocation. Never edit the user's MCP config.
        mcp = [
            "-c", "mcp_servers.stardew-companion.enabled=true",
            "-c", "mcp_servers.stardew-companion.required=true",
            "-c", "mcp_servers.stardew-companion.startup_timeout_sec=30",
            "-c", f'mcp_servers.stardew-companion.command={json.dumps(str(python))}',
            "-c", 'mcp_servers.stardew-companion.args=' + json.dumps([
                "-B", "-m", "stardew_ai_runtime.mcp_server", "--run-dir", str(self.run_dir),
                "--surface", "life" if os.environ.get("STARDEW_MCP_SURFACE") == "life" else "light",
            ]),
        ]
        # Codex sanitizes the environment inherited by MCP servers. Pass the
        # bridge's turn-scoped authority explicitly to this invocation only.
        for key in ("STARDEW_MCP_SURFACE", "STARDEW_LIFE_MODE",
                    "STARDEW_LIFE_PROPOSAL_ID", "STARDEW_LIFE_TURN_ID", "STARDEW_LIFE_SAVE_ID", "STARDEW_DECISION_TOKEN"):
            value = os.environ.get(key)
            if value is not None:
                mcp.extend(["-c", f'mcp_servers.stardew-companion.env.{key}={json.dumps(value)}'])
        cmd = [self.command, "exec"]
        if session_id:
            cmd.extend(["resume", session_id])
        # Resume accepts configuration overrides but not the new-session -s flag.
        # Keep this bridge-owned process in the same sandbox on every turn.
        cmd.extend(["--json", "--skip-git-repo-check", "-c", "approval_policy=never",
                    "-c", 'sandbox_mode="danger-full-access"',
                    *game_tool_config(), *inherited_mcp,
                    "-c", f"model_instructions_file={json.dumps(str(instructions_file))}", *mcp])
        if self.model:
            cmd.extend(["-m", self.model])
        if self.effort and self.effort != "default":
            cmd.extend(["-c", f'model_reasoning_effort={json.dumps(self.effort)}'])
        if self.http_provider:
            # Optional run-local workaround for a proxy which times out on the
            # Responses WebSocket transport. Keep auth, endpoint and approvals.
            cmd.extend(["-c", f"model_providers.{self.http_provider}.supports_websockets=false"])
        # Windows limits the entire command line to 32767 UTF-16 characters.
        # A legitimate compacted full snapshot can exceed that budget. The CLI
        # accepts '-' to read the identical prompt from stdin on new and resume.
        cmd.append("-")
        record_prepared(self.run_dir, active_task.request_id, session_id, prompt, instructions,
                        model=self.model, effort=self.effort, http_provider=self.http_provider)
        proc: Any = None
        try:
            proc = subprocess.Popen(cmd, cwd=str(self.run_dir), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                    stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
            active_task.process = proc
            assert proc.stdin is not None
            proc.stdin.write(prompt)
            proc.stdin.close()
            if self.progress:
                self.progress(BackendProgress(self.name, "started", "已启动 GPT"))
            events: queue.Queue[tuple[str, str | None]] = queue.Queue()

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
            done = set()
            reply = ""
            usage = None
            errors: list[str] = []
            terminal_failure: dict[str, Any] | None = None
            deadline = start + self.timeout_seconds
            while len(done) < 2 and time.monotonic() < deadline:
                if active_task.cancelled:
                    self.terminate(proc)
                    return BackendResult(False, "任务已取消。", session_id, provider=self.name,
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
                    # Readiness states are safe operational metadata. The full
                    # stderr can contain inherited configuration, never log it.
                    readiness = re.search(r"mcp:\s*stardew-companion\s+(starting|ready|failed)", line)
                    if readiness:
                        logger.info("Codex game MCP startup state=%s elapsedSeconds=%.3f", readiness.group(1), time.monotonic() - start)
                    continue
                try:
                    event = json.loads(line)
                except json.JSONDecodeError:
                    continue
                kind = event.get("type")
                if kind == "thread.started":
                    session_id = event.get("thread_id") or session_id
                    if session_id and self.progress:
                        self.progress(BackendProgress(self.name, "session_ready", session_id))
                elif kind == "item.completed":
                    item = event.get("item") or {}
                    if item.get("type") == "agent_message":
                        reply = str(item.get("text") or reply)
                    elif item.get("type") == "mcp_tool_call":
                        denial = self._approval_denial(item)
                        if denial:
                            # A CLI approval refusal never reached MCP. Stop this
                            # invocation, preserve policy, and surface the blocker.
                            self.terminate(proc)
                            tool = str(item.get("tool") or "MCP tool")
                            return BackendResult(False, f"工具 {tool} 被审批策略拒绝，未执行；自主工作已暂停。",
                                session_id, usage, {"code": "TOOL_APPROVAL_REQUIRED", "message": denial},
                                self.name, time.monotonic() - start).as_dict()
                        if self.progress:
                            self.progress(BackendProgress(self.name, "tool_started", "正在处理游戏任务",
                                                          item.get("tool")))
                elif kind == "turn.completed":
                    usage = event.get("usage")
                elif kind == "turn.failed":
                    errors.append(str(event.get("error") or "Codex turn failed"))
                    # Keep provider-origin failure evidence with the owned run.
                    # Never copy stderr/configuration, prompts or reasoning into
                    # this diagnostic; external CLI rollouts may disappear.
                    wire_error = event.get("error")
                    terminal_error = safe_error(wire_error)
                    terminal_failure = terminal_error
                    try:
                        error_log = self.run_dir / "logs" / "provider-errors.jsonl"
                        error_log.parent.mkdir(parents=True, exist_ok=True)
                        with error_log.open("a", encoding="utf-8") as output:
                            output.write(json.dumps({
                                "timestampUnix": time.time(), "provider": self.name,
                                "requestId": active_task.request_id, "conversationId": session_id,
                                "source": "codex-cli-turn.failed", "error": terminal_error,
                                "model": self.model, "effort": self.effort,
                                "httpProviderOverride": self.http_provider,
                            }, ensure_ascii=False) + "\n")
                    except OSError:
                        logger.warning("Could not persist provider terminal error evidence")
            if len(done) < 2:
                self.terminate(proc)
                return BackendResult(False, "GPT 执行超时，操作已中止。", session_id,
                                     error={"code": "TIMEOUT", "message": "Codex process deadline exceeded"},
                                     provider=self.name, duration=time.monotonic() - start).as_dict()
            proc.wait(timeout=2)
            if proc.returncode != 0 or not reply:
                detail = "".join(errors)[-500:]
                info = (terminal_failure or {}).get("codex_error_info")
                info_code = info.get("type") or info.get("code") if isinstance(info, dict) else info
                if (str((terminal_failure or {}).get("code") or info_code or "").lower() == "invalid_prompt"
                        or "flagged as potentially violating our usage policy" in str((terminal_failure or {}).get("message") or "").lower()):
                    return BackendResult(False,
                        "模型服务拒绝了本次请求（invalid_prompt）。已暂停自动重试，保留游戏进度。",
                        session_id, usage, {"code": "CODEX_INVALID_PROMPT", "message": detail},
                        self.name, time.monotonic() - start).as_dict()
                if (session_id and proc.returncode != 0
                        and any(f"no rollout found for thread id {session_id}" in error.lower()
                                for error in errors)):
                    return BackendResult(False, "GPT 本地会话文件已不存在，需要重新建立会话。",
                        session_id, usage, {"code": "CODEX_SESSION_UNAVAILABLE",
                                            "message": f"no rollout found for thread id {session_id}"},
                        self.name, time.monotonic() - start).as_dict()
                if session_id and any(
                        "context_length_exceeded" in error.lower()
                        or "ran out of room in the model's context window" in error.lower()
                        for error in errors):
                    return BackendResult(False, "GPT 会话上下文已满，需要重新建立会话。",
                        session_id, usage, {"code": "CODEX_CONTEXT_EXHAUSTED", "message": detail},
                        self.name, time.monotonic() - start).as_dict()
                return BackendResult(False, "GPT 请求失败，请稍后重试。", session_id, usage,
                                     {"code": "CODEX_REQUEST_FAILED", "message": detail}, self.name,
                                     time.monotonic() - start).as_dict()
            if self.progress:
                self.progress(BackendProgress(self.name, "completed", "GPT 已完成回复"))
            return BackendResult(True, reply, session_id, usage, provider=self.name,
                                 duration=time.monotonic() - start).as_dict()
        except Exception as ex:
            return BackendResult(False, "GPT 后端启动失败。", session_id,
                                 error={"code": "BACKEND_START_FAILED", "message": str(ex)},
                                 provider=self.name, duration=time.monotonic() - start).as_dict()
        finally:
            if proc is not None:
                try:
                    if proc.poll() is None:
                        self.terminate(proc)
                except Exception:
                    logger.exception("Failed to clean up Codex process")
            active_task.process = None
