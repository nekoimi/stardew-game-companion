from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
from typing import Any, Protocol


@dataclass(frozen=True, slots=True)
class BackendProgress:
    provider: str
    kind: str
    message: str
    tool_name: str | None = None


@dataclass(slots=True)
class BackendResult:
    success: bool
    reply: str = ""
    session_id: str | None = None
    usage: dict[str, Any] | None = None
    error: dict[str, str] | None = None
    provider: str = "unknown"
    duration: float = 0.0

    def as_dict(self) -> dict[str, Any]:
        result: dict[str, Any] = {
            "success": self.success,
            "response": self.reply,
            "conversation_id": self.session_id,
            "usage": self.usage,
            "provider": self.provider,
            "duration": self.duration,
        }
        if self.error:
            result["error"] = self.error.get("code") or self.error.get("message")
            result["error_detail"] = self.error
        return result


class ActiveTaskLike(Protocol):
    request_id: str
    cancelled: bool
    process: Any


ProgressCallback = Callable[[BackendProgress], None]
