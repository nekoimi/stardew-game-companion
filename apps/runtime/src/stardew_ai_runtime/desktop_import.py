"""Desktop command-line entry point for importing an existing installation."""

from .adapters.persistence.import_player_data import main

if __name__ == "__main__":
    raise SystemExit(main())
