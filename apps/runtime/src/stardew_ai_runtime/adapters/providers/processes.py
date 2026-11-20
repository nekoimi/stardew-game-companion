"""Lifecycle helpers for processes created by model adapters."""

import os
import subprocess


def terminate_owned_process(proc: subprocess.Popen) -> None:
    """Stop the process tree owned by the current invocation."""
    if os.name == "nt" and isinstance(getattr(proc, "pid", None), int) and proc.poll() is None:
        subprocess.run(
            ["taskkill", "/PID", str(proc.pid), "/T", "/F"],
            check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
    elif proc.poll() is None:
        proc.kill()
