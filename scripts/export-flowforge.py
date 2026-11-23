"""Export the FlowForge contract and a probe with a changed response type."""

import argparse
import copy
import hashlib
import json
from pathlib import Path

from flowforge.api import create_app


def encode(schema: dict) -> bytes:
    return (json.dumps(schema, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode(
        "utf-8"
    )


def check_references(value: object, schema: dict) -> int:
    if isinstance(value, dict):
        count = 0
        for key, item in value.items():
            if key == "$ref":
                if not isinstance(item, str) or not item.startswith("#/"):
                    raise ValueError(f"Expected a local JSON Pointer, got {item!r}")
                target = schema
                for part in item[2:].split("/"):
                    target = target[part.replace("~1", "/").replace("~0", "~")]
                count += 1
            else:
                count += check_references(item, schema)
        return count
    if isinstance(value, list):
        return sum(check_references(item, schema) for item in value)
    return 0


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="Directory for the two probe contracts")
    args = parser.parse_args()

    schema = create_app().openapi()
    original = encode(schema)
    if original != encode(create_app().openapi()):
        raise RuntimeError("Two exports of the same FlowForge source differ")
    references = check_references(schema, schema)
    operation = schema["paths"]["/api/v1/runs/{run_id}"]["get"]
    response = operation["responses"]["200"]["content"]["application/json"]["schema"]
    if response["$ref"] != "#/components/schemas/RunOutput":
        raise ValueError("The run detail response no longer uses RunOutput")

    changed = copy.deepcopy(schema)
    run_id = changed["components"]["schemas"]["RunOutput"]["properties"]["run_id"]
    if run_id.get("type") != "string" or run_id.get("format") != "uuid":
        raise ValueError("Expected RunOutput.run_id to be a UUID string")
    run_id["type"] = "integer"
    del run_id["format"]

    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "base.json").write_bytes(original)
    (args.output / "breaking.json").write_bytes(encode(changed))
    print(f"OpenAPI {schema['openapi']}, {len(schema['paths'])} paths, {references} local references")
    print(f"Two equal exports. SHA-256: {hashlib.sha256(original).hexdigest()}")


if __name__ == "__main__":
    main()
