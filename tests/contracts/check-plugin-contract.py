"""Validate the shared plugin package examples with JSON Schema."""

import hashlib
import json
from pathlib import Path

from jsonschema import Draft202012Validator
from referencing import Registry, Resource


def read(path):
    return json.loads(path.read_text(encoding="utf-8"))


root = Path(__file__).resolve().parents[2] / "contracts" / "plugin-host"
manifest_schema = read(root / "schemas" / "plugin.schema.json")
files_schema = read(root / "schemas" / "files.schema.json")
Draft202012Validator.check_schema(manifest_schema)
Draft202012Validator.check_schema(files_schema)
fixture = read(root / "examples" / "v1.json")["package"]
manifest = fixture["manifest"]
validator = Draft202012Validator(manifest_schema)
validator.validate(manifest)
assert manifest["hostProtocol"]["min"] <= 1 <= manifest["hostProtocol"]["max"]

payload = fixture["entryText"].encode("utf-8")
assert hashlib.sha256(payload).hexdigest() == fixture["entrySha256"]
manifest_bytes = json.dumps(manifest, ensure_ascii=False).encode("utf-8")
files = {
    "files": [
        {"path": "plugin.json", "sha256": hashlib.sha256(manifest_bytes).hexdigest()},
        {"path": manifest["entry"]["executable"], "sha256": fixture["entrySha256"]},
    ]
}
file_registry = Registry().with_resource("plugin.schema.json", Resource.from_contents(manifest_schema))
Draft202012Validator(files_schema, registry=file_registry).validate(files)

for variant in fixture["rejected"]:
    patched = {**manifest, **variant["patch"]}
    if variant["code"] == "invalid_request":
        assert not validator.is_valid(patched), variant["name"]
    else:
        validator.validate(patched)
        bounds = patched["hostProtocol"]
        assert not bounds["min"] <= 1 <= bounds["max"], variant["name"]

print("Plugin v1 package schemas, fixture hashes and rejection examples passed.")
