"""Antigravity CLI adapter for a game turn."""

from __future__ import annotations

import json
import logging
import os
import re
import time
from collections.abc import Callable
from pathlib import Path
from typing import Any, Protocol

from stardew_ai_runtime.application.provider_types import ActiveTaskLike

from .agy_database import check_new_quota_error
from .agy_process import run_agy_process, terminate_agy_process

logger = logging.getLogger(__name__)


class AgyTurn(ActiveTaskLike, Protocol):
    start_max_idx: int
    start_max_step_idx: int


class AgyBackend:
    name = "agy"
    terminate = staticmethod(terminate_agy_process)

    def __init__(self, run_dir: Path | None, receipt_dir: Path, command: str, model: str,
                 effort: str, failure: Callable[[str], None] | None = None):
        self.run_dir = run_dir
        self.receipt_dir = receipt_dir
        self.command = command
        self.model = model
        self.effort = effort
        self.failure = failure

    def _notify_failure(self, code: str) -> None:
        if self.failure is not None:
            self.failure(code)

    def run(
        self,
        active_task: AgyTurn,
        conversation_id: str | None,
        prompt: str,
    ) -> dict[str, Any]:
        """Executes agy CLI command synchronously in background thread with cancellation support."""
        requested_cid = conversation_id
        # AGY MCP workers outlive CLI turns. Refresh the exact authorization
        # through the same explicit per-run context mechanism used by other
        # persistent provider workers; never bind to the global common install.
        if self.run_dir:
            context = {key: os.environ[key] for key in
                       ("STARDEW_LIFE_MODE", "STARDEW_LIFE_TURN_ID", "STARDEW_LIFE_SAVE_ID", "STARDEW_DECISION_TOKEN")
                       if key in os.environ}
            path = Path(self.run_dir) / "data" / "agy-turn-context.json"
            path.parent.mkdir(parents=True, exist_ok=True)
            temp = path.with_suffix(".tmp")
            temp.write_text(json.dumps(context), encoding="utf-8")
            temp.replace(path)
        server = os.environ.get("STARDEW_AGY_GAME_SERVER")
        if server:
            prompt = f"本回合游戏工具仅来自 MCP 服务 {server}，对应当前已绑定隔离存档；不要调用其他游戏服务。\n" + prompt
        cmd = [self.command]

        # Keep the configured provider model on resume as well: agy otherwise
        # may route a saved conversation through its unrelated default model.
        if conversation_id:
            cmd.extend(["--conversation", conversation_id])
        if self.model:
            cmd.extend(["--model", self.model])

        # Explicit provider default supports models whose CLI rejects --effort.
        if self.effort and self.effort != "default":
            cmd.extend(["--effort", self.effort])

        cmd.extend([
            "--mode", "accept-edits",
            "--dangerously-skip-permissions",
            "--print-timeout", "10m",
        ])

        logger.info("Executing agy CLI: %s", " ".join(cmd[:6]) + " ...")
        start_time = time.monotonic()

        # Check BEFORE spawn (cancel race condition guard)
        if active_task.cancelled:
            logger.info("Task [%s] was cancelled before Popen spawn.", active_task.request_id)
            return {
                "success": False,
                "response": "任务已取消。",
                "error": "CANCELLED",
                "conversation_id": conversation_id,
                "duration": 0.0,
            }

        try:
            def on_conversation(cid: str) -> None:
                if cid != requested_cid:
                    active_task.start_max_idx = -1
                    active_task.start_max_step_idx = -1

            outcome = run_agy_process(active_task, cmd, prompt, conversation_id,
                run_dir=self.receipt_dir,
                model=self.model or "", start_idx=active_task.start_max_idx,
                timeout_seconds=600, on_conversation=on_conversation)
            conversation_id = outcome.conversation_id or conversation_id
            returncode = outcome.returncode
            stdout, stderr = outcome.stdout, outcome.stderr
            if outcome.timed_out:
                logger.error("agy CLI timed out after 600s")
                return {
                    "success": False,
                    "response": "执行超时 (10分钟)，操作已中止。",
                    "error": "TIMEOUT",
                    "conversation_id": conversation_id,
                    "duration": time.monotonic() - start_time,
                    "status": "interrupted",
                }

            duration = time.monotonic() - start_time
            stdout = stdout or ""
            stderr = stderr or ""

            if active_task.cancelled or outcome.cancelled:
                return {
                    "success": False,
                    "response": "任务已取消。",
                    "error": "CANCELLED",
                    "conversation_id": conversation_id,
                    "duration": duration,
                    "status": "interrupted",
                }

            # Parse JSON output if available
            parsed = None
            try:
                parsed = json.loads(stdout)
            except Exception:
                # Try finding JSON block in stdout if CLI emitted header/history text
                first_brace = stdout.find("{")
                last_brace = stdout.rfind("}")
                if first_brace != -1 and last_brace > first_brace:
                    candidate = stdout[first_brace:last_brace + 1]
                    try:
                        p = json.loads(candidate)
                        if isinstance(p, dict):
                            parsed = p
                    except Exception:
                        pass
                if parsed is None:
                    # Try line by line backwards
                    for line in reversed(stdout.splitlines()):
                        line = line.strip()
                        if line.startswith("{") and line.endswith("}"):
                            try:
                                p = json.loads(line)
                                if isinstance(p, dict):
                                    parsed = p
                                    break
                            except Exception:
                                pass

            cid = (parsed.get("conversation_id") if parsed else None) or conversation_id
            op_status = parsed.get("status") if parsed else None
            raw_response = parsed.get("response", "") if parsed else ""
            response_text = raw_response.strip() if isinstance(raw_response, str) else ""
            usage = parsed.get("usage") if parsed else None

            # Quota / Rate limit error checking using authoritative new steps baseline
            # Check for genuine fresh quota evidence:
            # 1. Authoritative DB steps check: new step_type=17 with idx > start_max_step_idx
            # Reset step baseline if agy established a new session / changed conversation_id
            step_baseline = -1 if (cid and requested_cid and cid != requested_cid) else active_task.start_max_step_idx
            is_new_quota_step, step_err_text = check_new_quota_error(
                cid, step_baseline
            )
            if is_new_quota_step:
                logger.error(
                    "Genuine Quota Exhausted detected in new step_type=17 step for [%s]: %s",
                    cid,
                    step_err_text[:300],
                )
                return {
                    "success": False,
                    "response": "AI 模型额度已用尽，任务已停止。请稍后或联系管理员补充额度。",
                    "error": "RESOURCE_EXHAUSTED",
                    "conversation_id": cid,
                    "duration": duration,
                }

            # 2. Check current process stderr (process-local, never cumulative)
            hard_quota_pattern = re.compile(
                r"(RESOURCE_EXHAUSTED|Resource has been exhausted|quota exceeded|Individual quota reached)",
                re.IGNORECASE,
            )
            rate_limit_pattern = re.compile(
                r"(429 Too Many Requests|rate limit exceeded|rate_limit)",
                re.IGNORECASE,
            )

            if stderr and hard_quota_pattern.search(stderr):
                logger.error("Genuine Quota Exhausted detected in stderr: %s", stderr[:300])
                self._notify_failure("RESOURCE_EXHAUSTED")
                return {
                    "success": False,
                    "response": "AI 模型额度已用尽，任务已停止。请稍后或联系管理员补充额度。",
                    "error": "RESOURCE_EXHAUSTED",
                    "conversation_id": cid,
                    "duration": duration,
                }

            if stderr and rate_limit_pattern.search(stderr):
                logger.warning("Transient Rate Limit detected in stderr: %s", stderr[:300])
                self._notify_failure("RATE_LIMIT_EXCEEDED")
                return {
                    "success": False,
                    "response": "AI 请求过于频繁 (Rate Limit)，请稍候重试。",
                    "error": "RATE_LIMIT_EXCEEDED",
                    "conversation_id": cid,
                    "duration": duration,
                }

            # 3. For new conversation only (not resumed), parsed.get("error") is fresh evidence
            is_new_session = requested_cid is None
            if is_new_session and parsed and parsed.get("error"):
                new_session_err = str(parsed.get("error"))
                if hard_quota_pattern.search(new_session_err):
                    logger.error("Quota Exhausted in new session error: %s", new_session_err[:300])
                    self._notify_failure("RESOURCE_EXHAUSTED")
                    return {
                        "success": False,
                        "response": "AI 模型额度已用尽，任务已停止。请稍后或联系管理员补充额度。",
                        "error": "RESOURCE_EXHAUSTED",
                        "conversation_id": cid,
                        "duration": duration,
                    }
                elif rate_limit_pattern.search(new_session_err):
                    self._notify_failure("RATE_LIMIT_EXCEEDED")
                    return {
                        "success": False,
                        "response": "AI 请求过于频繁 (Rate Limit)，请稍候重试。",
                        "error": "RATE_LIMIT_EXCEEDED",
                        "conversation_id": cid,
                        "duration": duration,
                    }

            # 4. If op_status is SUCCESS and response_text exists, current turn succeeded!
            # (Ignore any old cumulative error carried in parsed JSON)
            if op_status == "SUCCESS":
                if response_text:
                    return {
                        "success": True,
                        "response": response_text,
                        "conversation_id": cid,
                        "usage": usage,
                        "duration": duration,
                    }
                else:
                    logger.warning("agy returned SUCCESS but response was empty.")
                    return {
                        "success": False,
                        "response": "模型已执行但未返回具体汇报说明。",
                        "error": "EMPTY_MODEL_RESPONSE",
                        "conversation_id": cid,
                        "usage": usage,
                        "duration": duration,
                    }

            # 5. Non-zero exit code or failed status
            is_error = (returncode != 0) or (op_status not in ("SUCCESS", None))
            if is_error:
                cli_err = (parsed.get("error") or "") if is_new_session else ""
                err_text = f"{stderr}\n{cli_err}".strip()
                if err_text:
                    err_marker = err_text[:200]
                    resp = response_text if response_text else f"AI 执行出错 (退出码 {returncode})：{err_text[:200]}"
                else:
                    err_marker = f"AGY_EXIT_{returncode}" if returncode != 0 else f"AGY_STATUS_{op_status}"
                    resp = response_text if response_text else f"AI 执行出错 (退出码 {returncode})"
                logger.error("agy exited with error: code=%s, status=%s, err=%s", returncode, op_status, err_text[:300] or err_marker)
                return {
                    "success": False,
                    "response": resp,
                    "error": err_marker,
                    "conversation_id": cid,
                    "duration": duration,
                }

            # Plain text output fallback if proc.returncode == 0
            clean_stdout = stdout.strip()
            if returncode == 0 and clean_stdout:
                return {
                    "success": True,
                    "response": clean_stdout,
                    "conversation_id": conversation_id,
                    "duration": duration,
                }

            return {
                "success": False,
                "response": f"AI 未能正常生成回复 (code {returncode})",
                "error": "NO_OUTPUT",
                "conversation_id": conversation_id,
                "duration": duration,
            }
        except Exception as ex:
            logger.error("Failed to execute agy CLI: %s", ex, exc_info=True)
            return {
                "success": False,
                "response": f"调用后台 AI 服务发生异常：{ex}",
                "error": str(ex),
                "conversation_id": conversation_id,
            }
        finally:
            active_task.process = None
