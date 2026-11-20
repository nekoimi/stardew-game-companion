"""File operations shared by the per-save repositories."""

import json
import os
import time
import uuid
from collections.abc import Iterator
from contextlib import contextmanager, suppress
from pathlib import Path
from typing import Any


def read_json(path: Path, *, encoding: str = "utf-8") -> Any:
    return json.loads(path.read_text(encoding=encoding))


def replace_json(path: Path, value: Any, *, permission_retries: int = 0) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    pending = path.with_name(f"{path.name}.{uuid.uuid4().hex}.tmp")
    try:
        pending.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
        for attempt in range(permission_retries + 1):
            try:
                pending.replace(path)
                return
            except PermissionError:
                if attempt == permission_retries:
                    raise
                time.sleep(0.05 * (attempt + 1))
    finally:
        with suppress(OSError):
            pending.unlink(missing_ok=True)


@contextmanager
def lock_json(path: Path) -> Iterator[None]:
    """Use the existing sidecar lock so other runtime processes can share the store."""
    path.parent.mkdir(parents=True, exist_ok=True)
    lock_path = path.with_suffix(path.suffix + ".lock")
    with lock_path.open("a+b") as lock:
        if os.name == "nt":
            import msvcrt

            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_LOCK, 1)
            yield  # Closing the handle releases the byte-range lock.
        else:
            import fcntl

            fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
            yield  # Closing the handle releases the lock.
