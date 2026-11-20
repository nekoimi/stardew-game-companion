"""Manage the optional MiniMax Code adapter started by the desktop runtime."""

from __future__ import annotations

import asyncio
import contextlib
import json
import subprocess
import sys
from pathlib import Path

from .adapters.providers.mcode_external import AcpError, call_adapter, read_binding


class DesktopPartner:
    def __init__(self, run_dir: Path):
        self.run_dir = run_dir
        self.lock = asyncio.Lock()
        self.process: asyncio.subprocess.Process | None = None
        self.binding_id: str | None = None

    def active(self) -> bool:
        return self.process is not None and self.process.returncode is None

    async def launch(self, *, cwd: str, command: str, data_dir: str, session: str = "", name: str = ""):
        if not Path(cwd).is_absolute() or not Path(cwd).is_dir():
            raise AcpError("请选择原会话的工作目录。")
        if data_dir and (not Path(data_dir).is_absolute() or not Path(data_dir).is_dir()):
            raise AcpError("请选择有效的 MiniMax Code 数据目录。")
        args = ["--run-dir", str(self.run_dir), "--cwd", cwd, "--command", command, "--desktop-json"]
        if data_dir:
            args.extend(["--data-dir", data_dir])
        if session:
            args.extend(["--session", session, "--name", name or "原来的伙伴", "--original-client-idle"])
        else:
            args.append("--list")
        async with self.lock:
            if self.active():
                raise AcpError("已有桌面连接，请先断开原伙伴。")
            binding = read_binding(self.run_dir)
            if binding and binding.get("enabled", True):
                try:
                    await asyncio.to_thread(call_adapter, binding, "health")
                except (OSError, ValueError, AcpError):
                    pass
                else:
                    raise AcpError("已有客户端适配器在运行，请先在启动它的地方断开。")
            process = await asyncio.create_subprocess_exec(sys.executable, "-B", "-m",
                "stardew_ai_runtime.external_connection", *args,
                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            try:
                async with asyncio.timeout(65):
                    if not session:
                        output, errors = await process.communicate()
                        if process.returncode:
                            raise AcpError(errors.decode("utf-8", "replace").strip()[:500] or "无法读取原会话列表。")
                        sessions = json.loads(output)
                        if not isinstance(sessions, list):
                            raise ValueError("Expected an ACP session list")
                        return {"sessions": sessions}
                    ready = await process.stdout.readline()
                    if not ready:
                        errors = await process.stderr.read()
                        raise AcpError(errors.decode("utf-8", "replace").strip()[:500] or "无法验证原会话。")
                    self.binding_id = json.loads(ready)["bindingId"]
                    self.process = process
                    return {"attached": True}
            except (ValueError, KeyError) as ex:
                raise AcpError("MiniMax Code 返回的会话数据无效。") from ex
            except TimeoutError as ex:
                raise AcpError("MiniMax Code 未在预期时间内确认，请核对原客户端。") from ex
            finally:
                if self.process is not process and process.returncode is None:
                    with contextlib.suppress(ProcessLookupError):
                        process.kill()
                    await process.wait()

    async def stop(self) -> None:
        async with self.lock:
            process = self.process
            if process is None:
                return
            if self.active():
                binding = read_binding(self.run_dir)
                try:
                    if binding and binding.get("bindingId") == self.binding_id:
                        await asyncio.to_thread(call_adapter, binding, "disconnect")
                        await asyncio.wait_for(process.wait(), 7)
                    else:
                        raise AcpError("Owned adapter binding changed")
                except (OSError, TimeoutError, ValueError, AcpError):
                    if process.returncode is None:
                        with contextlib.suppress(ProcessLookupError):
                            process.kill()
                        await process.wait()
            self.process = None
            self.binding_id = None
