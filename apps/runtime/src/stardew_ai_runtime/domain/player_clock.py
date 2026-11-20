from __future__ import annotations

from typing import Any

PLAYER_DEFER_NON_SLEEP = 2330


def player_mode_clock_gate(operation: str, clock: Any, params: dict[str, Any] | None = None) -> str | None:
    """Reserve the remaining native night for rest, never freeze an action."""
    if type(clock) is not int or clock < PLAYER_DEFER_NON_SLEEP:
        return None
    if operation in {"sleep", "native_advance", "native_answer"} or operation.startswith(("observe_", "query_", "inspect_", "get_")):
        return None
    if operation == "navigate_to" and (params or {}).get("location_id") in {"Farm", "FarmHouse"}:
        return None
    return ("late-night: from 23:30 new unrelated work/travel waits until tomorrow. "
            "Use sleep for the native route to the player's own bed, confirmation and save; "
            "another NPC's bed/tent is not the player's sleep destination. "
            "Read-only observations and returning to Farm/FarmHouse remain available. "
            "Actions still consume normal game time; waiting until 01:30 can cause collapse en route.")
