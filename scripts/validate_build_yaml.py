#!/usr/bin/env python3
"""Validate build.yaml structure and content.

Checks that build.yaml:
  1. Parses as valid YAML (catches the exact class of error that broke releases)
  2. Contains all required fields
  3. Has a non-empty changelog without obvious malformation
  4. Version matches Directory.Build.props

Exit code: 0 if valid, 1 if issues found.
"""

import re
import sys

import yaml

ROOT_DIR = "build.yaml"
PROPS_FILE = "Directory.Build.props"

REQUIRED_FIELDS = [
    "name",
    "guid",
    "version",
    "targetAbi",
    "framework",
    "overview",
    "description",
    "category",
    "owner",
    "artifacts",
    "changelog",
]


def get_props_version() -> str | None:
    try:
        content = open(PROPS_FILE).read()
    except FileNotFoundError:
        return None
    match = re.search(r"<Version>([^<]+)</Version>", content)
    return match.group(1) if match else None


def main() -> int:
    errors: list[str] = []

    # 1. Parse YAML
    try:
        with open(ROOT_DIR) as f:
            data = yaml.safe_load(f)
    except yaml.YAMLError as e:
        print(f"FAIL: build.yaml is not valid YAML")
        print(f"  {e}")
        return 1

    if not isinstance(data, dict):
        print("FAIL: build.yaml root is not a mapping")
        return 1

    # 2. Required fields
    for field in REQUIRED_FIELDS:
        if field not in data:
            errors.append(f"missing required field: {field}")
        elif not data[field]:
            errors.append(f"field '{field}' is empty")

    # 3. Artifacts must be a non-empty list
    artifacts = data.get("artifacts")
    if isinstance(artifacts, list) and len(artifacts) == 0:
        errors.append("'artifacts' list is empty")

    # 4. Changelog sanity: should not look like two strings concatenated
    changelog = data.get("changelog", "")
    if isinstance(changelog, str):
        if changelog.count('".') > 1:
            errors.append(
                "changelog may contain a stray closing quote from bad concatenation"
            )
        if len(changelog) > 500:
            print(f"  WARNING: changelog is {len(changelog)} chars (consider trimming)")

    # 5. Version format (4-part dotted)
    version = data.get("version", "")
    if version and not re.match(r"^\d+\.\d+\.\d+\.\d+$", str(version)):
        errors.append(f"version '{version}' is not 4-part dotted format (e.g. 0.5.0.1)")

    # 6. Cross-check with Directory.Build.props
    props_ver = get_props_version()
    if props_ver and version and str(version) != props_ver:
        errors.append(f"build.yaml version ({version}) != Directory.Build.props ({props_ver})")

    # Report
    if errors:
        print("FAIL: build.yaml validation issues:")
        for e in errors:
            print(f"  - {e}")
        return 1

    print(f"PASS: build.yaml is valid (version={version}, changelog={len(changelog)} chars)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
