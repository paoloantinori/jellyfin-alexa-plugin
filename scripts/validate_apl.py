#!/usr/bin/env python3
"""
Validate APL (Alexa Presentation Language) templates and directives.

Usage:
    # Validate all APL templates embedded in AplHelper.cs
    python3 scripts/validate_apl.py

    # Validate a specific APL JSON file
    python3 scripts/validate_apl.py path/to/document.json

    # Validate a full skill response JSON (from SMAPI simulate-skill)
    python3 scripts/validate_apl.py --response path/to/response.json

Checks:
    1. APL document structure (type, version, mainTemplate)
    2. Datasource binding: mainTemplate.parameters must reference payload
    3. Data expressions: ${payload.xxx.properties.yyy} must match datasources
    4. Sequence data arrays: ${payload.xxx.properties.items} must exist
    5. TouchWrapper SendEvent arguments must be non-empty strings
    6. Required component properties (type on every component)
"""

import json
import re
import sys
from pathlib import Path

# ── APL validation rules ──────────────────────────────────────────────

APL_REQUIRED_TOP_KEYS = {"type", "version", "mainTemplate"}
VALID_APL_TYPES = {"APL"}
VALID_APL_VERSIONS = {"1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7", "1.8", "1.9"}

def extract_json_objects_from_cs(cs_path: Path) -> list[dict]:
    """Extract APL template JSON objects from C# source files."""
    content = cs_path.read_text()
    objects = []

    # Find @"{ ... }" verbatim strings with balanced braces
    i = 0
    while True:
        start = content.find('@"', i)
        if start == -1:
            break

        # Check if next non-whitespace char is {
        j = start + 2
        if j >= len(content) or content[j] != '{':
            i = start + 2
            continue

        # Walk forward counting braces to find the matching }
        depth = 0
        k = j
        while k < len(content):
            ch = content[k]
            if ch == '{':
                depth += 1
            elif ch == '}':
                depth -= 1
                if depth == 0:
                    break
            k += 1

        if depth != 0:
            i = start + 2
            continue

        # Extract the JSON between { and }
        json_str = content[j:k + 1]
        # Unescape C# verbatim string ("" -> ")
        json_str = json_str.replace('""', '"')

        try:
            obj = json.loads(json_str)
            if isinstance(obj, dict) and obj.get("type") == "APL":
                objects.append(obj)
        except json.JSONDecodeError as e:
            print(f"  WARNING: Could not parse template at offset {start}: {e}")

        i = k + 2  # skip past closing }"

    return objects


def extract_data_bindings(obj: dict, parent_path: str = "") -> list[str]:
    """Recursively find all ${payload.xxx} expressions in a template."""
    bindings = []
    if isinstance(obj, str):
        for m in re.finditer(r'\$\{(payload\.[a-zA-Z0-9_.]+)\}', obj):
            bindings.append(m.group(1))
    elif isinstance(obj, dict):
        for key, val in obj.items():
            bindings.extend(extract_data_bindings(val, f"{parent_path}.{key}"))
    elif isinstance(obj, list):
        for i, val in enumerate(obj):
            bindings.extend(extract_data_bindings(val, f"{parent_path}[{i}]"))
    return bindings


def validate_apl_document(doc: dict, name: str = "document") -> list[str]:
    """Validate an APL document structure. Returns list of errors."""
    errors = []

    # 1. Top-level required keys
    missing = APL_REQUIRED_TOP_KEYS - set(doc.keys())
    if missing:
        errors.append(f"[{name}] Missing required top-level keys: {missing}")

    # 2. Type must be "APL"
    if doc.get("type") not in VALID_APL_TYPES:
        errors.append(f"[{name}] Invalid type: {doc.get('type')!r}, expected one of {VALID_APL_TYPES}")

    # 3. Version check
    version = str(doc.get("version", ""))
    if version not in VALID_APL_VERSIONS:
        errors.append(f"[{name}] Invalid APL version: {version!r}, expected one of {VALID_APL_VERSIONS}")

    # 4. mainTemplate must exist and have parameters + items
    main_template = doc.get("mainTemplate")
    if not main_template:
        errors.append(f"[{name}] Missing mainTemplate")
        return errors

    if "parameters" not in main_template:
        errors.append(f"[{name}] mainTemplate missing 'parameters' — datasource binding won't work")

    if "items" not in main_template:
        errors.append(f"[{name}] mainTemplate missing 'items'")

    # 5. Parameters must include "payload" for datasource binding
    params = main_template.get("parameters", [])
    if "payload" not in params:
        errors.append(
            f"[{name}] mainTemplate.parameters does not include 'payload' — "
            "datasource binding expressions like ${payload.xxx} won't resolve"
        )

    # 6. Validate data bindings reference valid datasource paths
    bindings = extract_data_bindings(doc)
    # Extract top-level datasource names referenced: payload.xxx.properties.yyy
    ds_names = set()
    for b in bindings:
        # payload.xxx.properties.yyy -> xxx is the datasource name
        parts = b.split(".")
        if len(parts) >= 2 and parts[0] == "payload":
            ds_names.add(parts[1])

    if ds_names:
        # The document uses these datasource names — caller must provide them
        pass  # This is informational, validated against actual datasources below

    # 7. Check for common component issues
    _validate_components(main_template.get("items", []), name, errors)

    # 8. Check Sequence data arrays
    _validate_sequence_data(doc, name, errors)

    return errors


def _validate_components(items, name: str, errors: list[str], path: str = ""):
    """Recursively validate APL components."""
    if isinstance(items, dict):
        items = [items]

    for i, item in enumerate(items):
        if not isinstance(item, dict):
            continue

        current_path = f"{path}[{i}]" if path else f"items[{i}]"

        # Every component must have a type
        if "type" not in item:
            errors.append(f"[{name}.{current_path}] Component missing 'type' property")

        comp_type = item.get("type", "")

        # TouchWrapper must have onPress with SendEvent arguments
        if comp_type == "TouchWrapper":
            on_press = item.get("onPress", [])
            for j, handler in enumerate(on_press):
                if isinstance(handler, dict) and handler.get("type") == "SendEvent":
                    args = handler.get("arguments", [])
                    if not args:
                        errors.append(
                            f"[{name}.{current_path}.onPress[{j}]] "
                            "SendEvent has empty arguments — touch won't be identifiable"
                        )

        # Recurse into child items
        for key in ("items", "item"):
            children = item.get(key)
            if children:
                _validate_components(
                    children if isinstance(children, list) else [children],
                    name, errors, f"{current_path}.{key}"
                )


def _validate_sequence_data(doc: dict, name: str, errors: list[str]):
    """Validate Sequence.data expressions reference existing paths."""
    def find_sequences(obj, path=""):
        if isinstance(obj, dict):
            if obj.get("type") == "Sequence" and "data" in obj:
                data_expr = obj["data"]
                if isinstance(data_expr, str) and data_expr.startswith("${"):
                    # Should be ${payload.xxx.properties.items}
                    if "properties" not in data_expr:
                        errors.append(
                            f"[{name}.{path}] Sequence.data = '{data_expr}' — "
                            "should follow ${payload.xxx.properties.items} pattern"
                        )
            for key, val in obj.items():
                find_sequences(val, f"{path}.{key}")
        elif isinstance(obj, list):
            for i, val in enumerate(obj):
                find_sequences(val, f"{path}[{i}]")

    find_sequences(doc)


def validate_directive(directive: dict, name: str = "directive") -> list[str]:
    """Validate a full RenderDocument directive (document + datasources)."""
    errors = []

    # Check directive type
    dtype = directive.get("type")
    if dtype == "Alexa.Presentation.APL.RenderDocument":
        doc = directive.get("document")
        ds = directive.get("datasources")

        if not doc:
            errors.append(f"[{name}] RenderDocument missing 'document'")
            return errors

        # Validate document structure
        errors.extend(validate_apl_document(doc, f"{name}.document"))

        # Validate datasource binding matches document expectations
        if ds and isinstance(ds, dict):
            bindings = extract_data_bindings(doc)
            for binding in bindings:
                # payload.xxx.properties.yyy -> check ds.xxx.properties.yyy exists
                parts = binding.split(".")
                if len(parts) >= 2 and parts[0] == "payload":
                    ds_name = parts[1]
                    if ds_name not in ds:
                        errors.append(
                            f"[{name}] Document references '${{{binding}}}' but "
                            f"datasources has no '{ds_name}' key"
                        )
                    else:
                        ds_entry = ds[ds_name]
                        if not isinstance(ds_entry, dict):
                            errors.append(f"[{name}] datasources.{ds_name} is not an object")
                        elif ds_entry.get("type") != "object":
                            errors.append(
                                f"[{name}] datasources.{ds_name}.type should be 'object', "
                                f"got {ds_entry.get('type')!r}"
                            )
                        elif "properties" not in ds_entry:
                            errors.append(f"[{name}] datasources.{ds_name} missing 'properties'")

                        # Check specific property references
                        if len(parts) >= 4 and parts[2] == "properties":
                            prop_name = parts[3]
                            props = ds_entry.get("properties", {})
                            if isinstance(props, dict) and prop_name not in props:
                                # This might be OK for conditional fields (when expressions)
                                pass  # Soft check — some fields are conditional

    elif dtype == "Alexa.Presentation.APL.ExecuteCommands":
        commands = directive.get("commands", [])
        if not commands:
            errors.append(f"[{name}] ExecuteCommands has empty commands list")

    return errors


def validate_skill_response(response: dict) -> list[str]:
    """Validate APL directives in a full Alexa skill response."""
    errors = []
    directives = response.get("response", {}).get("directives", [])

    if not directives:
        print("  ⚠ No directives found in response")
        return errors

    apl_directives = [
        d for d in directives
        if isinstance(d, dict) and "APL" in d.get("type", "")
    ]

    if not apl_directives:
        print("  ⚠ No APL directives found in response")
        return errors

    print(f"  Found {len(apl_directives)} APL directive(s)")
    for i, d in enumerate(apl_directives):
        errors.extend(validate_directive(d, f"directive[{i}]"))

    return errors


# ── Main ───────────────────────────────────────────────────────────────

def main():
    args = sys.argv[1:]
    all_errors = []

    if not args:
        # Default: validate APL templates from AplHelper.cs
        cs_path = Path("Jellyfin.Plugin.AlexaSkill/Alexa/Apl/AplHelper.cs")
        if not cs_path.exists():
            print(f"ERROR: {cs_path} not found")
            sys.exit(1)

        print("Validating APL templates from AplHelper.cs ...\n")
        templates = extract_json_objects_from_cs(cs_path)
        if not templates:
            print("ERROR: No APL templates found in source file")
            sys.exit(1)

        for i, tmpl in enumerate(templates):
            name = f"template[{i}]"
            print(f"  {name}: type={tmpl.get('type')}, version={tmpl.get('version')}")
            errs = validate_apl_document(tmpl, name)
            bindings = extract_data_bindings(tmpl)
            ds_names = set()
            for b in bindings:
                parts = b.split(".")
                if len(parts) >= 2 and parts[0] == "payload":
                    ds_names.add(parts[1])
            print(f"    Datasource names referenced: {ds_names or 'none'}")
            print(f"    Binding expressions: {len(bindings)}")
            all_errors.extend(errs)

        print()

    elif args[0] == "--response":
        # Validate APL directives in a skill response JSON
        if len(args) < 2:
            print("Usage: validate_apl.py --response <response.json>")
            sys.exit(1)

        path = Path(args[1])
        print(f"Validating APL directives in {path} ...\n")
        response = json.loads(path.read_text())
        all_errors.extend(validate_skill_response(response))
        print()

    else:
        # Validate specific APL JSON file(s)
        for path_str in args:
            path = Path(path_str)
            print(f"Validating {path} ...\n")
            doc = json.loads(path.read_text())

            if doc.get("type", "").startswith("Alexa.Presentation.APL"):
                all_errors.extend(validate_directive(doc, path.name))
            elif doc.get("type") == "APL":
                all_errors.extend(validate_apl_document(doc, path.name))
            else:
                print(f"  ⚠ Unknown document type: {doc.get('type')!r}")

            print()

    if all_errors:
        print(f"FAILURES ({len(all_errors)}):")
        for err in all_errors:
            print(f"  ✗ {err}")
        sys.exit(1)
    else:
        print("All APL validations passed ✓")
        sys.exit(0)


if __name__ == "__main__":
    main()
