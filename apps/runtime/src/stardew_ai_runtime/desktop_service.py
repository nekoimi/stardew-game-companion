"""Desktop runtime integration and local service lifecycle."""

from __future__ import annotations

import argparse
import asyncio
import json
import logging
import os
import socket
from pathlib import Path

import uvicorn

from .desktop_api import create_app
from .desktop_runtime import DesktopBridge, DesktopError
from .owner_process import OwnerProcessGuard
from .provider_registry import PROVIDERS
from .release_package import verify_release


async def serve(bridge: DesktopBridge, token: str, stop: asyncio.Event):
    with socket.create_server(("127.0.0.1", 0)) as listener:
        port = listener.getsockname()[1]
        config = uvicorn.Config(create_app(bridge, token), access_log=False, ws="none", timeout_graceful_shutdown=1)
        server = uvicorn.Server(config)
        runtime_task = asyncio.create_task(bridge.run(stop))
        web_task = asyncio.create_task(server.serve(sockets=[listener]))
        try:
            while not server.started and not web_task.done():
                await asyncio.sleep(0.03)
            if not server.started:
                raise DesktopError("本地控制服务未启动。")
            print(json.dumps({"port": port}), flush=True)
            done, _ = await asyncio.wait({runtime_task, web_task}, return_when=asyncio.FIRST_COMPLETED)
            for task in done:
                task.result()
        finally:
            stop.set()
            server.should_exit = True
            runtime_task.cancel()
            await asyncio.gather(runtime_task, web_task, return_exceptions=True)
            await bridge.partner.stop()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--run-dir", required=True, type=Path)
    parser.add_argument("--owner-pid", required=True, type=int)
    parser.add_argument("--model", default="")
    parser.add_argument("--effort", default="")
    parser.add_argument("--backend", choices=[spec.id for spec in PROVIDERS.values() if spec.desktop], default="pi")
    parser.add_argument("--client", default="")
    parser.add_argument("--pi-cmd", default="", help=argparse.SUPPRESS)
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO)
    token = os.environ.pop("STARDEW_DESKTOP_TOKEN", "")
    if len(token) < 32:
        parser.error("Desktop launcher token missing")
    guard = OwnerProcessGuard(args.owner_pid)
    try:
        verify_release(args.run_dir)
        client = ("" if os.getenv("STARDEW_CONNECTION_SOURCE") == "external"
                  else PROVIDERS[args.backend].check_client(args.run_dir, args.client or args.pi_cmd))
        bridge = DesktopBridge(args.run_dir, args.model, client, backend=args.backend, effort=args.effort)
        asyncio.run(guard.run(lambda stop: serve(bridge, token, stop)))
    except (ValueError, OSError, DesktopError) as ex:
        print(json.dumps({"error": str(ex)}, ensure_ascii=True), flush=True)
        raise SystemExit(1) from ex
    finally:
        guard.close()


if __name__ == "__main__":
    main()
