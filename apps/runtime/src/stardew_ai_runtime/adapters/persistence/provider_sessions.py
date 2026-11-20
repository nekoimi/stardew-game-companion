"""Read and write provider session records in the existing on-disk format."""

import logging
from pathlib import Path

from .json_file import read_json, replace_json

logger = logging.getLogger(__name__)


class ProviderSessionStore:
    def __init__(self, bindings_path: Path, history_path: Path):
        self.bindings_path = bindings_path
        self.history_path = history_path

    def load_bindings(self) -> dict[str, str]:
        if self.bindings_path.is_file():
            try:
                data = read_json(self.bindings_path)
                if isinstance(data, dict):
                    return {str(key): str(value) for key, value in data.items()}
            except (OSError, ValueError) as error:
                logger.warning("Failed to load chat sessions from %s: %s", self.bindings_path, error)
        return {}

    def save_bindings(self, sessions: dict[str, str]) -> None:
        try:
            replace_json(self.bindings_path, sessions)
        except (OSError, ValueError) as error:
            logger.warning("Failed to save chat sessions to %s: %s", self.bindings_path, error)

    def load_history(self) -> dict[str, list[str]]:
        if self.history_path.is_file():
            try:
                data = read_json(self.history_path)
                if isinstance(data, dict):
                    return {str(key): [str(item) for item in values]
                            for key, values in data.items() if isinstance(values, list)}
            except (OSError, ValueError):
                logger.debug("Failed to load session history", exc_info=True)
        return {}

    def save_history(self, history: dict[str, list[str]]) -> None:
        try:
            replace_json(self.history_path, history)
        except (OSError, ValueError):
            logger.debug("Failed to save session history", exc_info=True)
