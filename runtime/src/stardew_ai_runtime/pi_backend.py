"""Pi RPC adapter. Provider sessions persist; each game turn owns its process.

Reopening RPC for each turn gives the game MCP fresh authority/environment and
releases its native connection before the next turn. No user Pi files are changed.
"""
from __future__ import annotations

import contextlib
import json
import os
import queue
import shutil
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Any

from .agent_backends import BackendProgress, BackendResult, KimiBackend
from .agent_instructions import runtime_instructions


class PiBackend:
    name = "pi"

    def __init__(self, run_dir: Path, model: str = "", command: str | list[str] = "pi",
                 progress=None, timeout_seconds: float = 600, effort: str | None = "default"):
        self.run_dir = Path(run_dir).resolve()
        self.model, self.command = model, command
        self.progress, self.timeout_seconds = progress, timeout_seconds
        if effort not in {None, "default", "off", "minimal", "low", "medium", "high", "xhigh", "max"}:
            raise ValueError("Unsupported pi thinking level")
        self.effort = effort

    @staticmethod
    def terminate(proc: Any) -> None:
        """Request RPC cancellation before terminating this owned process tree."""
        cancel = getattr(proc, "stardew_pi_abort", None)
        settled = getattr(proc, "stardew_pi_settled", None)
        if proc.poll() is None and callable(cancel) and settled is not None and not settled.is_set():
            try:
                cancel()
                settled.wait(timeout=2)
            except (OSError, ValueError):
                pass
        KimiBackend.terminate(proc)

    def _executable(self) -> list[str]:
        if isinstance(self.command, list):
            return list(self.command)
        executable = shutil.which(self.command)
        if not executable:
            raise FileNotFoundError("Install and sign in to Pi first")
        if Path(executable).suffix.lower() in {".cmd", ".ps1"}:
            package = Path(executable).parent / "node_modules" / "@earendil-works" / "pi-coding-agent"
            manifest = json.loads((package / "package.json").read_text(encoding="utf-8"))
            entry = manifest["bin"]
            entry = entry["pi"] if isinstance(entry, dict) else entry
            adjacent = Path(executable).parent / "node.exe"
            node = str(adjacent) if adjacent.is_file() else shutil.which("node")
            if not node or not (package / entry).is_file():
                raise FileNotFoundError("Pi Node.js entry is unavailable")
            return [node, str(package / entry)]
        return [executable]

    def _prepare(self, task, session_id: str | None) -> tuple[list[str], Path]:
        workspace = self.run_dir / "data" / "pi"
        sessions = workspace / "sessions"
        sessions.mkdir(parents=True, exist_ok=True)
        if session_id and not Path(session_id).is_file():
            raise FileNotFoundError("PI_SESSION_UNAVAILABLE")
        python = self.run_dir / "runtime" / "python" / "python.exe"
        python = python if python.is_file() else Path(sys.executable)
        context = {key: os.environ[key] for key in (
            "STARDEW_LIFE_MODE", "STARDEW_LIFE_TURN_ID", "STARDEW_LIFE_SAVE_ID",
            "STARDEW_LIFE_PROPOSAL_ID", "STARDEW_DECISION_TOKEN", "PYTHONPATH") if key in os.environ}
        context["STARDEW_MCP_SURFACE"] = "light"
        context.setdefault("PYTHONPATH", str(Path(__file__).resolve().parents[1]))
        config = workspace / ".pi" / "mcp.json"
        config.parent.mkdir(parents=True, exist_ok=True)
        document = {"mcpServers": {"stardew-companion": {
            "command": str(python), "args": ["-B", "-m", "stardew_ai_runtime.mcp_server",
                "--run-dir", str(self.run_dir), "--surface", "light"],
            "env": context, "cwd": str(self.run_dir), "exposure": "direct",
            "timeout": self.timeout_seconds,
        }}}
        temporary = config.with_suffix(".tmp")
        temporary.write_text(json.dumps(document, ensure_ascii=False, indent=2), encoding="utf-8")
        temporary.replace(config)
        instructions = workspace / "game-instructions.md"
        instructions.write_text(getattr(task, "instructions_text", None) or runtime_instructions(), encoding="utf-8")
        command = self._executable() + ["--mode", "rpc", "--approve", "--session-dir", str(sessions),
                                        "--append-system-prompt", str(instructions)]
        if session_id:
            command += ["--session", session_id]
        if self.model:
            command += ["--model", self.model]
        if self.effort not in {None, "default"}:
            command += ["--thinking", self.effort]
        return command, workspace

    def run(self, active_task, session_id: str | None, prompt: str) -> dict[str, Any]:
        started = time.monotonic()
        proc = None
        cid, reply, usage = session_id, "", None
        code, message = None, None
        compacted = False
        active_task.provider_input_delivered = False
        events: queue.Queue = queue.Queue()
        settled = threading.Event()
        write_lock = threading.Lock()
        readers = []

        def send(kind, **data):
            with write_lock:
                proc.stdin.write(json.dumps({"id": kind, "type": kind, **data}, ensure_ascii=False) + "\n")
                proc.stdin.flush()

        def read_stdout():
            try:
                for line in proc.stdout:
                    try:
                        event = json.loads(line)
                    except ValueError:
                        events.put({"type": "protocol_error"})
                        continue
                    if not isinstance(event, dict):
                        events.put({"type": "protocol_error"})
                        continue
                    if event.get("type") == "agent_settled":
                        settled.set()
                    events.put(event)
            finally:
                events.put(None)

        def drain_stderr():
            # Consume continuously without storing credentials or unbounded output.
            while proc.stderr.read(4096):
                pass

        try:
            command, workspace = self._prepare(active_task, session_id)
            if active_task.cancelled:
                raise InterruptedError
            proc = subprocess.Popen(command, cwd=workspace, env=dict(os.environ), stdin=subprocess.PIPE,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace",
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            proc.stardew_pi_abort = lambda: send("abort")
            proc.stardew_pi_settled = settled
            active_task.process = proc
            for target in (read_stdout, drain_stderr):
                reader = threading.Thread(target=target, daemon=True, name=f"pi-rpc-{proc.pid}-{target.__name__}")
                reader.start()
                readers.append(reader)
            send("get_state")
            deadline = time.monotonic() + self.timeout_seconds
            prompted = False
            run_error = False
            seen_messages = set()
            while True:
                if active_task.cancelled:
                    raise InterruptedError
                if time.monotonic() > deadline:
                    raise TimeoutError
                try:
                    event = events.get(timeout=0.05)
                except queue.Empty:
                    continue
                if event is None:
                    raise RuntimeError("PI_RUNTIME_EXITED")
                deadline = time.monotonic() + self.timeout_seconds
                kind = event.get("type")
                if kind == "compaction_end" and event.get("result") and not event.get("aborted"):
                    compacted = True
                if kind == "protocol_error":
                    raise RuntimeError("PI_RPC_INVALID")
                if kind == "response":
                    if event.get("success") is not True:
                        raise RuntimeError("PI_RPC_REJECTED")
                    if event.get("id") == "get_state":
                        actual = (event.get("data") or {}).get("sessionFile")
                        if not actual or (session_id and Path(actual).resolve() != Path(session_id).resolve()):
                            raise RuntimeError("PI_SESSION_CHANGED")
                        if not session_id and not Path(actual).resolve().is_relative_to(workspace / "sessions"):
                            raise RuntimeError("PI_SESSION_CHANGED")
                        cid = str(Path(actual).resolve())
                        active_task.provider_conversation_id = cid
                        send("prompt", message=prompt)
                        prompted = True
                    continue
                if kind == "message_end":
                    msg = event.get("message") or {}
                    if msg.get("role") == "user" and prompted:
                        content = msg.get("content", [])
                        text = content if isinstance(content, str) else "".join(
                            part.get("text", "") for part in content if part.get("type") == "text")
                        if text == prompt:
                            active_task.provider_input_delivered = True
                    if msg.get("role") == "assistant":
                        reply = "".join(p.get("text", "") for p in msg.get("content", []) if p.get("type") == "text")
                        run_error = msg.get("stopReason") in {"error", "aborted"}
                        identity = json.dumps(msg, sort_keys=True, ensure_ascii=False)
                        if identity not in seen_messages and isinstance(msg.get("usage"), dict):
                            seen_messages.add(identity)
                            raw = msg["usage"]
                            fields = {"input_tokens": ("input", "cacheRead", "cacheWrite"),
                                      "output_tokens": ("output",), "cache_read_tokens": ("cacheRead",),
                                      "cache_creation_tokens": ("cacheWrite",)}
                            for key, parts in fields.items():
                                values = [raw.get(part) for part in parts]
                                if all(type(v) is int and v >= 0 for v in values):
                                    usage = usage or {"source": "pi_assistant_message", "input_includes_cache": True,
                                                      "generations_count": 0}
                                    usage[key] = usage.get(key, 0) + sum(values)
                            if usage:
                                usage["generations_count"] += 1
                                if "input_tokens" in usage and "output_tokens" in usage:
                                    usage["total_tokens"] = usage["input_tokens"] + usage["output_tokens"]
                if self.progress and kind in {"message_update", "tool_execution_start", "tool_execution_end", "turn_start", "compaction_start"}:
                    self.progress(BackendProgress("pi", kind, "正在执行游戏回合", event.get("toolName")))
                if kind == "agent_settled" and prompted:
                    if event.get("aborted") or run_error:
                        raise RuntimeError("PI_TURN_FAILED")
                    if not active_task.provider_input_delivered:
                        raise RuntimeError("PI_INPUT_UNCONFIRMED")
                    break
        except InterruptedError:
            code, message = "PLAYER_CANCELLED", "本轮 pi 执行已取消。"
        except TimeoutError:
            code, message = "TIMEOUT", "pi 长时间没有响应；请先核对当前游戏动作再继续。"
        except FileNotFoundError as ex:
            code = "PI_SESSION_UNAVAILABLE" if str(ex) == "PI_SESSION_UNAVAILABLE" else "PI_NOT_FOUND"
            message = "原 pi 会话不可用，绑定已保留。" if code == "PI_SESSION_UNAVAILABLE" else "请安装并登录支持 MCP 和 RPC 的 Pi CLI。"
        except (OSError, ValueError, KeyError, RuntimeError) as ex:
            code = str(ex) if str(ex).startswith("PI_") else "PI_START_FAILED"
            message = "pi 游戏回合未完成，请检查客户端版本、登录与连接；未切换会话。"
        finally:
            if proc is not None:
                self.terminate(proc)
                try:
                    proc.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    pass
                for reader in readers:
                    reader.join(timeout=1)
                for stream in (proc.stdin, proc.stdout, proc.stderr):
                    with contextlib.suppress(OSError, ValueError):
                        stream.close()
            active_task.process = None
        result = BackendResult(code is None, reply or message or "", cid, usage,
            {"code": code, "message": message} if code else None, "pi", time.monotonic() - started).as_dict()
        result["usage_source"] = "pi_assistant_message" if usage else "unknown"
        result["context_compacted"] = compacted
        return result
