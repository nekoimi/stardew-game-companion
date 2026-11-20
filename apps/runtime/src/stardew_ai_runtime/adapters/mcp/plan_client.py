from __future__ import annotations

import asyncio
import contextlib
import json
import logging
import os
import sys
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)


class InternalMcpPlanClient:
    """Persistent internal MCP client the bridge owns for plan execution.

    It speaks to the *same* MCP server implementation the model uses (spawned
    with the harness-only ``--surface internal`` — the light game surface plus the
    read-only reconcile tool — and the same ``--run-dir``), so the worker path uses
    the same policy/budget/chest checks and the same real scheduler code as a
    normal tool call — it is not a second independent scheduler socket next to a
    provider.

    The Mod transport accepts a single command socket and answers a concurrent
    connection with 409 Conflict, so ownership is serialized: the bridge closes
    this client before starting a provider turn and reopens it afterwards.

    The stdio session lives in one dedicated asyncio task that opens, calls and
    closes it in the same task context (the only supported usage for
    ``stdio_client``'s cancel scopes).
    """

    def __init__(
        self,
        run_dir: str | Path | None,
        *,
        python_executable: str | None = None,
        timeout_seconds: float = 120.0,
    ):
        self.run_dir = Path(run_dir) if run_dir else None
        self.python_executable = python_executable or sys.executable
        self.timeout_seconds = timeout_seconds
        self._task: asyncio.Task | None = None
        self._loop: asyncio.AbstractEventLoop | None = None
        self._requests: asyncio.Queue | None = None
        self._ready: asyncio.Future | None = None
        self._closed = False
        self._live_session: Any | None = None
        self._close_lock = asyncio.Lock()

    @property
    def connected(self) -> bool:
        return bool(self._ready and self._ready.done() and not self._ready.cancelled())

    def build_command(self) -> list[str]:
        if self.run_dir is None:
            raise RuntimeError("internal plan client requires a run directory")
        return [
            self.python_executable,
            "-m",
            "stardew_ai_runtime.mcp_server",
            "--run-dir",
            str(self.run_dir),
            "--surface",
            "internal",
        ]

    async def open(self) -> None:
        # An idle worker may be closing the old session while a player control
        # needs a new one. Never let that close clear the new session's state.
        async with self._close_lock:
            await self._open_session()

    async def _open_session(self) -> None:
        """Open while holding the lifecycle lock, including native control calls."""
        if self._task is None or self._task.done():
            self._loop = asyncio.get_running_loop()
            self._requests = asyncio.Queue()
            self._ready = self._loop.create_future()
            self._closed = False
            self._task = self._loop.create_task(self._session_loop())
        await asyncio.wait_for(asyncio.shield(self._ready), timeout=self.timeout_seconds)

    async def call_tool(self, name: str, arguments: dict[str, Any] | None = None) -> Any:
        # Native controls must reach the existing scheduler while a long native
        # operation is awaiting its terminal result. The read that determines
        # whether cancellation is needed must not queue behind that job either.
        if name in {"cancel_plan_command", "reconcile_plan_command"} or name == "dispatch_plan_operation" and (arguments or {}).get("operation") in {"cancel_task", "pause_task", "resume_task"}:
            # Receiving a terminal can make the worker idle while this read is
            # still pending. Keep that idle close from interrupting the reply.
            async with self._close_lock:
                await self._open_session()
                result = await asyncio.wait_for(self._live_session.call_tool(name, arguments or {}), timeout=10)
                if getattr(result, "isError", False):
                    raise RuntimeError(f"Native control rejected: {_tool_result_payload(result)}")
                return _tool_result_payload(result)
        await self.open()
        return await self._command(("call", name, arguments or {}))

    async def current_save_id(self) -> str | None:
        try:
            status = await self.call_tool("get_status", {"detail": False})
        except Exception:
            logger.debug("Unable to read save id for plan worker", exc_info=True)
            return None
        save_id = status.get("saveId") if isinstance(status, dict) else None
        if not save_id or save_id == "unknown":
            return None
        return str(save_id)

    async def reconcile(self, command_id: str) -> Any:
        """Ask the same server to reconcile a persisted command id (read-only)."""
        return await self.call_tool("reconcile_plan_command", {"command_id": command_id})

    async def close(self) -> None:
        """Release the internal execution socket so a provider turn can own it."""
        async with self._close_lock:
            if self._task is None:
                return
            try:
                if not self._task.done():
                    await asyncio.wait_for(self._command(("close", None, None)), timeout=5.0)
            except Exception:
                logger.debug("Internal MCP client close command failed", exc_info=True)
            task, self._task = self._task, None
            if not task.done():
                try:
                    await asyncio.wait_for(task, timeout=5.0)
                except (TimeoutError, asyncio.CancelledError):
                    task.cancel()
                    with contextlib.suppress(Exception, asyncio.CancelledError):
                        await task
                except Exception:
                    logger.debug("Internal MCP session task ended with error", exc_info=True)
            self._requests = None
            self._ready = None


    # ------------------------------------------------------------- internals
    async def _command(self, command: tuple[str, Any, Any]) -> Any:
        if self._requests is None or self._loop is None or self._closed:
            raise RuntimeError("internal MCP client is not open")
        future: asyncio.Future = self._loop.create_future()
        self._requests.put_nowait((command, future))
        return await future

    async def _session_loop(self) -> None:
        """Own the stdio session for its whole lifetime in one task."""
        from mcp import ClientSession, StdioServerParameters
        from mcp.client.stdio import stdio_client

        cmd = self.build_command()
        env = {**os.environ, "STARDEW_MCP_SURFACE": "internal"}
        try:
            async with stdio_client(
                StdioServerParameters(command=cmd[0], args=cmd[1:], env=env)
            ) as (read_stream, write_stream):
                async with ClientSession(read_stream, write_stream) as session:
                    await session.initialize()
                    self._live_session = session
                    self._resolve_ready(None)
                    try:
                        await self._serve(session)
                    finally:
                        self._live_session = None
        except BaseException as ex:  # noqa: BLE001 - reported to the awaiting caller
            self._resolve_ready(ex)
            if not isinstance(ex, (asyncio.CancelledError, GeneratorExit)):
                logger.warning("Internal MCP session ended: %s", ex)
        finally:
            self._closed = True
            self._fail_queued_requests()

    def _fail_queued_requests(self) -> None:
        queue = self._requests
        if queue is None:
            return
        while not queue.empty():
            _, future = queue.get_nowait()
            if not future.done():
                future.set_exception(RuntimeError("Internal MCP session closed before request completed; native outcome is unknown."))

    def _resolve_ready(self, error: BaseException | None) -> None:
        ready = self._ready
        if ready is not None and not ready.done():
            if error is None:
                ready.set_result(True)
            else:
                ready.set_exception(error)

    async def _serve(self, session: Any) -> None:
        queue = self._requests
        if queue is None:
            return
        while True:
            command, future = await queue.get()
            kind, name, arguments = command
            if kind == "close":
                if not future.done():
                    future.set_result(None)
                return
            try:
                result = await asyncio.wait_for(
                    session.call_tool(name, arguments or {}),
                    timeout=self.timeout_seconds,
                )
                if getattr(result, "isError", False):
                    raise RuntimeError(
                        f"internal MCP tool '{name}' failed: {_tool_result_payload(result)}"
                    )
                payload = _tool_result_payload(result)
            except BaseException as ex:  # noqa: BLE001 - release caller even on cancellation
                if not future.done():
                    future.set_exception(
                        RuntimeError("Internal MCP session interrupted; native outcome is unknown.")
                        if isinstance(ex, (asyncio.CancelledError, GeneratorExit)) else ex
                    )
                if not isinstance(ex, Exception):
                    raise
            else:
                if not future.done():
                    future.set_result(payload)

    # Kept for explicit teardown paths (bridge shutdown) where awaiting is unsafe.
    def terminate_sync(self) -> None:
        task, self._task = self._task, None
        if task is not None and not task.done():
            task.cancel()


def _tool_result_payload(result: Any) -> Any:
    """Normalise an MCP CallToolResult to its structured JSON payload."""
    structured = getattr(result, "structuredContent", None)
    if isinstance(structured, dict):
        if set(structured) == {"result"}:
            return structured["result"]
        return structured
    content = getattr(result, "content", None)
    if isinstance(content, list):
        for item in content:
            text = getattr(item, "text", None)
            if isinstance(text, str):
                try:
                    return json.loads(text)
                except ValueError:
                    return text
    return None
