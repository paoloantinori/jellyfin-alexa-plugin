#!/usr/bin/env python3
"""Validate locale JSON files for key coverage and structural correctness.

Ensures every locale has the same set of response string keys as en-US
(the reference locale). A missing key causes a runtime KeyNotFoundException
when ResponseStrings.Get() is called with that key for that locale.

Uses a baseline file (locale_baseline.json) to track known pre-existing gaps.
CI only fails on NEW missing keys not in the baseline.

Also guards value content where wording is load-bearing: the FavoriteNoItem
apology must name both the add and the remove wording in every locale (the
JF-824 direction guard). Direction findings are warnings: printed in full in
every mode, they do not affect the exit code.

Modes:
  --check    Validate against baseline (default, for CI)
  --full     Fail on ALL missing keys, ignoring baseline
  --diff     Show only NEW gaps not in baseline

Exit code: 0 if all checks pass, 1 if any new error found.
"""

import json
import re
import sys
import unicodedata
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
LOCALE_DIR = SCRIPT_DIR.parent / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "Locale"
BASELINE_PATH = SCRIPT_DIR / "locale_baseline.json"
REFERENCE_LOCALE = "en-US"

# JF-824: per-locale direction-word guard for FavoriteNoItem. The apology must
# name BOTH the add and the remove wording (direction neutrality, JF-821); the
# presence checks above cannot see a translation edit flipping e.g. it-IT
# "aggiungere o rimuovere" to an add-only "aggiungere". Words are sourced
# verbatim from the committed values; a deliberate rewording updates its row in
# the same change. Matching is case- and Unicode-normalization-insensitive,
# with letter boundaries for ASCII words (see contains_direction_word), so a
# capitalization-only rewording stays green while 'add' inside 'address'
# cannot satisfy the guard. Warning-level per this file's value-content
# convention (empty values / extra keys warn; errors are reserved for
# structural breaks), the same design as validate_interaction_models check #10.
FAVORITE_NO_ITEM_DIRECTION_WORDS: dict[str, tuple[str, str]] = {
    "ar-SA": ("إضافته", "إزالته"),
    "de-DE": ("hinzufügen", "entfernen"),
    "en-AU": ("add", "remove"),
    "en-CA": ("add", "remove"),
    "en-GB": ("add", "remove"),
    "en-IN": ("add", "remove"),
    "en-US": ("add", "remove"),
    "es-ES": ("añadir", "quitar"),
    "es-MX": ("añadir", "quitar"),
    "es-US": ("añadir", "quitar"),
    "fr-CA": ("ajouter", "retirer"),
    "fr-FR": ("ajouter", "retirer"),
    "hi-IN": ("जोड़ा", "हटाया"),
    "it-IT": ("aggiungere", "rimuovere"),
    "ja-JP": ("追加", "削除"),
    "nl-NL": ("toevoegen", "verwijderen"),
    "pt-BR": ("adicionar", "remover"),
}


def contains_direction_word(folded_value: str, word: str) -> bool:
    """Case- and normalization-insensitive presence of one direction word.

    ``folded_value`` is already NFC-normalized and casefolded by the caller.
    ASCII words match with ASCII-letter boundaries so 'add' cannot match
    inside 'address'; non-ASCII scripts (ja/hi/ar) keep plain substring
    matching because \\b boundaries do not exist between their characters.
    Inflected forms ('added') do not match; a rewording that uses one updates
    the table row in the same change.
    """
    w = unicodedata.normalize("NFC", word).casefold()
    if w.isascii():
        return re.search(rf"(?<![A-Za-z]){re.escape(w)}(?![A-Za-z])", folded_value) is not None
    return w in folded_value


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
    direction_findings: list[str] = []
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

        # JF-824: FavoriteNoItem direction-word guard (warning; see table above)
        locale_direction: list[str] = []
        favorite_no_item = data.get("FavoriteNoItem")
        if isinstance(favorite_no_item, str) and favorite_no_item.strip():
            folded = unicodedata.normalize("NFC", favorite_no_item).casefold()
            pair = FAVORITE_NO_ITEM_DIRECTION_WORDS.get(locale)
            if pair is None:
                locale_direction.append(
                    f"  [{locale}] FavoriteNoItem present but no direction-word row in "
                    "FAVORITE_NO_ITEM_DIRECTION_WORDS (add one in the same change)"
                )
            else:
                lost = [w for w in pair if not contains_direction_word(folded, w)]
                if lost:
                    locale_direction.append(
                        f"  [{locale}] FavoriteNoItem lost direction wording: missing "
                        f"{' and '.join(lost)} (must name both add and remove)"
                    )
        direction_findings.extend(locale_direction)

        parts = []
        if new_missing:
            parts.append(f"{len(new_missing)} NEW missing")
        if known_missing & missing:
            parts.append(f"{len(known_missing & missing)} known gaps")
        if extra:
            parts.append(f"{len(extra)} extra")
        if locale_direction:
            parts.append("direction wording")
        status = ", ".join(parts) if parts else "OK"
        print(f"  [{locale}] {status}")

    # Summary
    print(f"\n{'='*60}")
    if all_warnings and mode != "--diff":
        print(f"WARN: {len(all_warnings)} known gap(s) / extra key(s)")
        if mode == "--full":
            for w in all_warnings[:10]:
                print(w)

    # Direction findings are never baseline-known gaps, so they always print
    # in full (including --diff and --check) instead of hiding in the count.
    if direction_findings:
        print(f"WARN: {len(direction_findings)} FavoriteNoItem direction-word finding(s):")
        for w in direction_findings:
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
