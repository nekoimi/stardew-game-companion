"""Events and dispatch policy supplied by the runtime host."""

from __future__ import annotations

from typing import TYPE_CHECKING, Protocol

if TYPE_CHECKING:
    from stardew_ai_runtime.protocol import Envelope


class RuntimeEvents(Protocol):
    def game_loaded(self, session_id: str | None) -> None: ...

    def game_connected(self, save_id: str | None) -> None: ...

    async def game_disconnected(self) -> None: ...

    def publish_reply(self, reply: Envelope) -> None: ...

    def native_event(self, envelope: dict, save_id: str | None) -> None: ...

    def permits_dispatch(self) -> bool: ...

    def control_pending(self) -> bool: ...
