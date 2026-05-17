#!/usr/bin/env python3
"""Validate locale JSON files for key coverage and structural correctness.

Ensures every locale has the same set of response string keys as en-US
(the reference locale). A missing key causes a runtime KeyNotFoundException
when ResponseStrings.Get() is called with that key for that locale.

Uses a baseline file (locale_baseline.json) to track known pre-existing gaps.
CI only fails on NEW missing keys not in the baseline.

Modes:
  --check    Validate against baseline (default, for CI)
  --full     Fail on ALL missing keys, ignoring baseline
  --diff     Show only NEW gaps not in baseline

Exit code: 0 if all checks pass, 1 if any new error found.
"""

import json
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
LOCALE_DIR = SCRIPT_DIR.parent / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "Locale"
BASELINE_PATH = SCRIPT_DIR / "locale_baseline.json"
REFERENCE_LOCALE = "en-US"


def load_locale(path: Path) -> dict | None:
    """Load a locale JSON file, returning None on parse error."""
    try:
        with open(path) as f:
            return json.load(f)
    except json.JSONDecodeError as e:
        print(f"  FAIL: {path.name}: Invalid JSON: {e}")
        return None


def load_baseline() -> dict[str, list[str]]:
    """Load the known-gaps baseline."""
    if BASELINE_PATH.exists():
        return json.load(open(BASELINE_PATH))
    return {}


def main() -> int:
    mode = "--check"
    if "--full" in sys.argv:
        mode = "--full"
    elif "--diff" in sys.argv:
        mode = "--diff"

    locale_files = sorted(LOCALE_DIR.glob("*.json"))
    if not locale_files:
        print(f"FAIL: No locale JSON files found in {LOCALE_DIR}")
        return 1

    ref_path = LOCALE_DIR / f"{REFERENCE_LOCALE}.json"
    if not ref_path.exists():
        print(f"FAIL: Reference locale {REFERENCE_LOCALE} not found")
        return 1

    ref_data = load_locale(ref_path)
    if ref_data is None:
        return 1

    ref_keys = set(ref_data.keys())
    baseline = load_baseline() if mode != "--full" else {}
    print(f"Reference locale {REFERENCE_LOCALE}: {len(ref_keys)} keys")
    if baseline:
        known = sum(len(v) for v in baseline.values())
        print(f"Baseline: {known} known gaps across {len(baseline)} locales")

    all_errors: list[str] = []
    all_warnings: list[str] = []
    total_known = 0

    for path in locale_files:
        locale = path.stem
        data = load_locale(path)
        if data is None:
            all_errors.append(f"  [{locale}] Could not parse file")
            continue

        keys = set(data.keys())

        # Check for missing keys
        missing = ref_keys - keys
        known_missing = set(baseline.get(locale, []))
        new_missing = missing - known_missing

        total_known += len(known_missing & missing)

        if mode == "--full":
            # Fail on ALL missing keys
            for k in sorted(missing):
                all_errors.append(f"  [{locale}] Missing key '{k}'")
        else:
            # Only fail on NEW missing keys (not in baseline)
            for k in sorted(new_missing):
                all_errors.append(f"  [{locale}] NEW missing key '{k}' (not in baseline)")
            # Known gaps are warnings
            for k in sorted(known_missing & missing):
                all_warnings.append(f"  [{locale}] Known gap '{k}'")

        # Check for extra keys (warning only)
        extra = keys - ref_keys
        if extra:
            for k in sorted(extra):
                all_warnings.append(f"  [{locale}] Extra key '{k}' (not in {REFERENCE_LOCALE})")

        # Check for empty values (warning)
        for k in sorted(keys & ref_keys):
            val = data.get(k, "")
            if isinstance(val, str) and not val.strip():
                all_warnings.append(f"  [{locale}] Empty value for key '{k}'")

        parts = []
        if new_missing:
            parts.append(f"{len(new_missing)} NEW missing")
        if known_missing & missing:
            parts.append(f"{len(known_missing & missing)} known gaps")
        if extra:
            parts.append(f"{len(extra)} extra")
        status = ", ".join(parts) if parts else "OK"
        print(f"  [{locale}] {status}")

    # Summary
    print(f"\n{'='*60}")
    if all_warnings and mode != "--diff":
        print(f"WARN: {len(all_warnings)} known gap(s) / extra key(s)")
        if mode == "--full":
            for w in all_warnings[:10]:
                print(w)

    if all_errors:
        print(f"FAIL: {len(all_errors)} NEW missing key(s) not in baseline:")
        for e in all_errors:
            print(e)
        return 1

    label = "all locales consistent" if mode == "--full" else "no new locale gaps"
    print(f"PASS: {label}")
    if total_known:
        print(f"  ({total_known} known pre-existing gaps in baseline)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
