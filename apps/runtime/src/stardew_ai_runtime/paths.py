from pathlib import Path


def project_root(module_file: str) -> Path:
    source = Path(module_file).resolve()
    for parent in source.parents:
        if (parent / "release-manifest.json").is_file() or (parent / "Directory.Build.props").is_file():
            return parent
    # Older runtime layouts and compatibility fixtures can omit the markers.
    return source.parents[3]
