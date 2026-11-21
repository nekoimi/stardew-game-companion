from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any

from .paths import local_config_path
from .paths import project_root as locate_project_root

DEFAULT_BACKEND = "kimi"
DEFAULT_MODEL = "kimi-code/k3"


def project_root() -> Path:
    return locate_project_root(__file__)


def config_path() -> Path:
    override = os.getenv("STARDEW_CHAT_BACKEND_CONFIG")
    if override:
        return Path(override)
    return local_config_path(project_root(), "chat-backend.json")


def load_chat_backend_config(path: Path | None = None) -> dict[str, str]:
    from .provider_registry import PROVIDERS

    target = path or config_path()
    try:
        raw: Any = json.loads(target.read_text(encoding="utf-8"))
        if isinstance(raw, dict) and isinstance(raw.get("backend"), str) and raw["backend"] in PROVIDERS:
            spec = PROVIDERS[raw["backend"]]
            config = {"backend": spec.id, "model": str(raw.get("model") or spec.default_model)}
            if spec.default_effort != "default" or spec.id == "mcode":
                config["effort"] = spec.default_effort
            if raw.get("effort") in spec.efforts:
                config["effort"] = str(raw["effort"])
            if raw["backend"] == "codex" and isinstance(raw.get("httpProvider"), str):
                config["httpProvider"] = raw["httpProvider"]
            if raw.get("agent"):
                config["agent"] = str(raw["agent"])
            if raw.get("agentFile"):
                config["agentFile"] = str(raw["agentFile"])
            return config
    except (OSError, ValueError, TypeError):
        pass
    return {"backend": DEFAULT_BACKEND, "model": DEFAULT_MODEL}
