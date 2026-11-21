from pathlib import Path


def project_root(module_file: str) -> Path:
    source = Path(module_file).resolve()
    for parent in source.parents:
        if (parent / "release-manifest.json").is_file() or (parent / "Directory.Build.props").is_file():
            return parent
    # Older runtime layouts and compatibility fixtures can omit the markers.
    return source.parents[3]


def local_config_path(root: Path, name: str) -> Path:
    """Resolve checkout settings while retaining the installed Mod layout."""
    legacy = root / "config" / name
    if (root / "release-manifest.json").is_file():
        return legacy
    local = root / ".local" / name
    return legacy if not local.is_file() and legacy.is_file() else local
