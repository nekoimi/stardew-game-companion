"""Names shared by the live state, save checkpoints and offline import."""

from pathlib import Path

WORK_STATE = "work-state.json"
AUTONOMY_STATE = "autonomy-state.json"
COMPANION_PROFILE = "companion-profile.json"
COMPANION_MEMORY = "companion-memory.json"
COMPANION_CARE = "companion-care.json"
COMPANION_MILESTONES = "companion-milestones.json"
SESSION_BINDINGS = "chat_sessions.json"
RUNTIME_FILES = (
    WORK_STATE, AUTONOMY_STATE, COMPANION_PROFILE, COMPANION_MEMORY, COMPANION_CARE, COMPANION_MILESTONES,
)


def data_path(run_dir: Path | str, name: str) -> Path:
    return Path(run_dir) / "data" / name
