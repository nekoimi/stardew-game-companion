"""Command-line entry point for continuing an original MiniMax Code session."""

from .adapters.providers.mcode_external import main

if __name__ == "__main__":
    raise SystemExit(main())
