#!/usr/bin/env python3
"""Validate version consistency across project files.

Checks that version numbers agree across:
  - Directory.Build.props (<Version>)
  - build.yaml (version)
  - manifest.json (latest entry version)

Exit code: 0 if versions match, 1 if mismatch found.
"""

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def get_props_version() -> str | None:
    """Extract version from Directory.Build.props."""
    props = ROOT / "Directory.Build.props"
    if not props.exists():
        return None
    content = props.read_text()
    match = re.search(r"<Version>([^<]+)</Version>", content)
    return match.group(1) if match else None


def get_build_yaml_version() -> str | None:
    """Extract version from build.yaml."""
    build_yaml = ROOT / "build.yaml"
    if not build_yaml.exists():
        return None
    for line in build_yaml.read_text().splitlines():
        if line.startswith("version:"):
            return line.split(":", 1)[1].strip().strip('"')
    return None


def get_manifest_version() -> str | None:
    """Extract latest version from manifest.json."""
    manifest = ROOT / "manifest.json"
    if not manifest.exists():
        return None
    try:
        data = json.load(open(manifest))
        if isinstance(data, list) and data:
            versions = data[0].get("versions", [])
            if versions:
                return versions[-1].get("version")
    except (json.JSONDecodeError, KeyError, IndexError):
        pass
    return None


def main() -> int:
    props_ver = get_props_version()
    yaml_ver = get_build_yaml_version()
    manifest_ver = get_manifest_version()

    versions = {
        "Directory.Build.props": props_ver,
        "build.yaml": yaml_ver,
        "manifest.json (latest)": manifest_ver,
    }

    print("Version sources:")
    for source, ver in versions.items():
        print(f"  {source}: {ver or 'NOT FOUND'}")

    # Check that all found versions agree
    found = {v for v in versions.values() if v is not None}
    if not found:
        print("FAIL: No version found in any file")
        return 1

    if len(found) == 1:
        print(f"\nPASS: All versions match ({found.pop()})")
        return 0

    # Show mismatches
    print(f"\nFAIL: Version mismatch detected!")
    props_v = props_ver or "?"
    yaml_v = yaml_ver or "?"
    manifest_v = manifest_ver or "?"

    if props_ver and yaml_ver and props_ver != yaml_ver:
        print(f"  Directory.Build.props ({props_v}) != build.yaml ({yaml_v})")
    if props_ver and manifest_ver and props_ver != manifest_ver:
        print(f"  Directory.Build.props ({props_v}) != manifest.json ({manifest_v})")
    if yaml_ver and manifest_ver and yaml_ver != manifest_ver:
        print(f"  build.yaml ({yaml_v}) != manifest.json ({manifest_v})")

    return 1


if __name__ == "__main__":
    sys.exit(main())
