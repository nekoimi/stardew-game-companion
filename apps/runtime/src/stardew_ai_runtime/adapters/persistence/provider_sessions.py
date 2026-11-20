"""Read and write provider session records in the existing on-disk format."""

import json
import logging
from pathlib import Path

logger = logging.getLogger(__name__)


class ProviderSessionStore:
    def __init__(self, bindings_path: Path, history_path: Path):
        self.bindings_path = bindings_path
        self.history_path = history_path

    def load_bindings(self) -> dict[str, str]:
        if self.bindings_path.is_file():
            try:
                data = json.loads(self.bindings_path.read_text(encoding="utf-8"))
                if isinstance(data, dict):
                    return {str(key): str(value) for key, value in data.items()}
            except (OSError, ValueError) as error:
                logger.warning("Failed to load chat sessions from %s: %s", self.bindings_path, error)
        return {}

    def save_bindings(self, sessions: dict[str, str]) -> None:
        try:
            self._write(self.bindings_path, sessions)
        except (OSError, ValueError) as error:
            logger.warning("Failed to save chat sessions to %s: %s", self.bindings_path, error)

    def load_history(self) -> dict[str, list[str]]:
        if self.history_path.is_file():
            try:
                data = json.loads(self.history_path.read_text(encoding="utf-8"))
                if isinstance(data, dict):
                    return {str(key): [str(item) for item in values]
                            for key, values in data.items() if isinstance(values, list)}
            except (OSError, ValueError):
                logger.debug("Failed to load session history", exc_info=True)
        return {}

    def save_history(self, history: dict[str, list[str]]) -> None:
        try:
            self._write(self.history_path, history)
        except (OSError, ValueError):
            logger.debug("Failed to save session history", exc_info=True)

    @staticmethod
    def _write(path: Path, data: dict) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
