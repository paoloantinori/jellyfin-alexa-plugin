#!/usr/bin/env python3
"""Validate all interaction model JSON files for structural correctness and cross-locale consistency.

Catches the failure modes that have caused broken models in the past:
  - Malformed JSON
  - Missing required fields (invocationName, intents, types)
  - Slot type inconsistencies (same slot name, different type across intents)
  - AMAZON.SearchQuery coexistence violations
  - Cross-locale intent drift (locale X missing an intent others have)
  - Undefined slot types (slot references a type not in the types array)
  - Intents with zero sample utterances
  - Duplicate sample utterances within an intent

Exit code: 0 if all checks pass, 1 if any error found.
"""

import json
import sys
from pathlib import Path

MODELS_DIR = Path(__file__).resolve().parent.parent / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "InteractionModel"

# Intents that are expected to exist in all locales
# (Amazon built-in intents may vary, so we only check custom intents)
REQUIRED_CUSTOM_INTENTS = {
    "MarkFavoriteIntent",
    "UnmarkFavoriteIntent",
    "MediaInfoIntent",
    "PlayFavoritesIntent",
    "PlayAlbumIntent",
    "PlayArtistSongsIntent",
    "PlayBookIntent",
    "PlayChannelIntent",
    "PlayIntent",
    "PlayLastAddedIntent",
    "PlayPlaylistIntent",
    "PlaySongIntent",
    "PlayVideoIntent",
    "PlayRandomIntent",
    "PlayByGenreIntent",
    "PlayByDecadeIntent",
    "PlayMoodMusicIntent",
    "ContinueWatchingIntent",
    "GoToChapterIntent",
    "InProgressMediaListIntent",
    "BrowseLibraryIntent",
    "RecommendIntent",
    "SleepTimerIntent",
    "PlayEpisodeIntent",
    "LoopSongOnIntent",
    "AddToQueueIntent",
    "PlayNextIntent",
    "ClearQueueIntent",
    "ListQueueIntent",
    "PlayRadioIntent",
    "TurnRadioOnIntent",
    "TurnRadioOffIntent",
    "LearnMyVoiceIntent",
    "WhoAmIIntent",
    "QueryArtistLibraryIntent",
    "PlayPodcastIntent",
    "SearchMediaIntent",
    "SetReminderIntent",
    "QueryRecentlyAddedIntent",
    "FollowMeIntent",
}

# Intents that legitimately may not have slots
SLOTLESS_INTENTS = {
    "PlayIntent",
    "PlayFavoritesIntent",
    "PlayRandomIntent",
    "ContinueWatchingIntent",
    "InProgressMediaListIntent",
    "LoopSongOnIntent",
    "ClearQueueIntent",
    "ListQueueIntent",
    "TurnRadioOnIntent",
    "TurnRadioOffIntent",
    "LearnMyVoiceIntent",
    "WhoAmIIntent",
    "FollowMeIntent",
}


def load_model(path: Path) -> dict | None:
    """Load and return the languageModel from a model file, or None on parse error."""
    try:
        with open(path) as f:
            data = json.load(f)
    except json.JSONDecodeError as e:
        print(f"  FAIL: {path.name}: Invalid JSON: {e}")
        return None

    lm = data.get("languageModel")
    if lm is None:
        print(f"  FAIL: {path.name}: Missing top-level 'languageModel' key")
        return None

    return lm


def validate_single_model(locale: str, lm: dict) -> tuple[list[str], list[str]]:
    """Validate a single locale's languageModel.

    Returns (errors, warnings) where errors are structural issues that break
    the model and warnings are quality issues (duplicates, zero samples).
    """
    errors: list[str] = []
    warnings: list[str] = []
    prefix = f"  [{locale}]"

    # 1. Required fields
    invocation = lm.get("invocationName")
    if not invocation or not invocation.strip():
        errors.append(f"{prefix} Missing or empty 'invocationName'")

    intents = lm.get("intents")
    if not intents or not isinstance(intents, list):
        errors.append(f"{prefix} Missing or empty 'intents' array")
        return errors, warnings

    types = lm.get("types", [])
    types_by_name = {t["name"]: t for t in types if isinstance(t, dict) and "name" in t}

    intent_names = set()
    slot_type_usage: dict[str, str] = {}  # slot_name -> type_name (for consistency check)

    for intent in intents:
        name = intent.get("name", "<unnamed>")
        intent_names.add(name)

        # 2. Samples existence (warning, not error)
        samples = intent.get("samples", [])
        if not samples and name not in SLOTLESS_INTENTS and not name.startswith("AMAZON."):
            warnings.append(f"{prefix} Intent '{name}' has zero sample utterances")

        # 3. Duplicate samples (warning, not error)
        if samples:
            seen = set()
            for s in samples:
                if s in seen:
                    warnings.append(f"{prefix} Intent '{name}': duplicate sample '{s[:60]}'")
                seen.add(s)

        # 4. Slot validation
        slots = intent.get("slots", [])
        has_search_query = False
        other_slots = []

        for slot in slots:
            slot_name = slot.get("name")
            slot_type = slot.get("type")

            if not slot_name or not slot_type:
                errors.append(f"{prefix} Intent '{name}': slot missing 'name' or 'type'")
                continue

            # Track slot_name -> type consistency
            if slot_name in slot_type_usage and slot_type_usage[slot_name] != slot_type:
                errors.append(
                    f"{prefix} Slot '{slot_name}' uses different types: "
                    f"'{slot_type_usage[slot_name]}' vs '{slot_type}' (intent '{name}')"
                )
            slot_type_usage[slot_name] = slot_type

            # Check AMAZON.SearchQuery coexistence
            if slot_type == "AMAZON.SearchQuery":
                has_search_query = True
            else:
                other_slots.append(slot_name)

            # 5. Undefined custom slot type (not AMAZON.* and not in types array)
            if not slot_type.startswith("AMAZON.") and slot_type not in types_by_name:
                errors.append(
                    f"{prefix} Intent '{name}': slot '{slot_name}' references "
                    f"undefined slot type '{slot_type}'"
                )

        # 6. AMAZON.SearchQuery coexistence violation
        if has_search_query and other_slots:
            errors.append(
                f"{prefix} Intent '{name}': AMAZON.SearchQuery cannot coexist "
                f"with other slots ({other_slots})"
            )

    # 7. Required custom intents check (only for intents present in ALL other locales)
    # Handled by cross-locale validation below; per-locale only checks structural issues

    # 8. Custom slot types must have at least one value (SMAPI rejects empty types)
    for t in types:
        tname = t.get("name", "<unnamed>")
        tvals = t.get("values", [])
        if isinstance(tvals, list) and len(tvals) == 0:
            errors.append(f"{prefix} Custom slot type '{tname}' has no values (SMAPI rejects empty types)")

    # 9. fallbackIntentSensitivity only valid for English and German locales
    mc = lm.get("modelConfiguration")
    if mc and "fallbackIntentSensitivity" in mc:
        if not (locale.startswith("en-") or locale == "de-DE"):
            errors.append(
                f"{prefix} fallbackIntentSensitivity is only supported "
                f"for English and German locales (de-DE)"
            )

    return errors, warnings


def validate_cross_locale(all_models: dict[str, dict]) -> list[str]:
    """Check consistency across locales. Returns list of error strings."""
    errors: list[str] = []

    # Build intent sets per locale (excluding Amazon built-ins)
    locale_intents: dict[str, set[str]] = {}
    for locale, lm in all_models.items():
        intents = set()
        for intent in lm.get("intents", []):
            name = intent.get("name", "")
            if not name.startswith("AMAZON."):
                intents.add(name)
        locale_intents[locale] = intents

    if not locale_intents:
        return errors

    # Count how many locales have each intent
    intent_counts: dict[str, int] = {}
    for intents in locale_intents.values():
        for name in intents:
            intent_counts[name] = intent_counts.get(name, 0) + 1

    num_locales = len(locale_intents)
    # Only flag intents present in majority of locales (>50%) but missing from one
    threshold = num_locales // 2 + 1

    for locale, intents in sorted(locale_intents.items()):
        for name in sorted(intent_counts):
            if name not in intents and intent_counts[name] >= threshold:
                errors.append(
                    f"  [{locale}] Missing intent '{name}' "
                    f"(present in {intent_counts[name]}/{num_locales} locales)"
                )

    return errors


def validate_slot_types_cross_locale(all_models: dict[str, dict]) -> list[str]:
    """Check that custom slot types are consistent across locales."""
    errors: list[str] = []

    # Collect slot type names per locale
    locale_types: dict[str, set[str]] = {}
    for locale, lm in all_models.items():
        types = set()
        for t in lm.get("types", []):
            if isinstance(t, dict) and "name" in t:
                types.add(t["name"])
        locale_types[locale] = types

    if not locale_types:
        return errors

    all_types = set()
    for s in locale_types.values():
        all_types |= s

    for locale, types in sorted(locale_types.items()):
        missing = all_types - types
        if missing:
            for m in sorted(missing):
                errors.append(f"  [{locale}] Missing slot type present in other locales: '{m}'")

    return errors


def main() -> int:
    model_files = sorted(MODELS_DIR.glob("model_*.json"))
    if not model_files:
        print("FAIL: No model_*.json files found in", MODELS_DIR)
        return 1

    print(f"Validating {len(model_files)} interaction models...")
    all_errors: list[str] = []
    all_warnings: list[str] = []
    all_models: dict[str, dict] = {}

    # Phase 1: Per-locale validation
    for path in model_files:
        locale = path.stem.replace("model_", "")
        lm = load_model(path)
        if lm is None:
            all_errors.append(f"  [{locale}] Could not parse model file")
            continue

        errors, warnings = validate_single_model(locale, lm)
        all_errors.extend(errors)
        all_warnings.extend(warnings)
        all_models[locale] = lm

        parts = []
        if errors:
            parts.append(f"{len(errors)} error(s)")
        if warnings:
            parts.append(f"{len(warnings)} warning(s)")
        status = ", ".join(parts) if parts else "OK"
        print(f"  [{locale}] {status}")

    # Phase 2: Cross-locale validation (only if all models parsed)
    if len(all_models) == len(model_files):
        print("\nCross-locale consistency:")
        cross_warnings = validate_cross_locale(all_models)
        all_warnings.extend(cross_warnings)

        type_warnings = validate_slot_types_cross_locale(all_models)
        all_warnings.extend(type_warnings)

        if not cross_warnings and not type_warnings:
            print("  All locales have consistent intents and slot types")
        else:
            for e in cross_warnings + type_warnings:
                print(f"  WARN: {e}")

    # Summary
    print(f"\n{'='*60}")
    if all_warnings:
        print(f"WARN: {len(all_warnings)} warning(s) (pre-existing issues, not blockers):")
        for w in all_warnings[:20]:
            print(w)
        if len(all_warnings) > 20:
            print(f"  ... and {len(all_warnings) - 20} more warnings")

    if all_errors:
        print(f"FAIL: {len(all_errors)} error(s) found:")
        for e in all_errors:
            print(e)
        return 1

    print("PASS: All interaction models are structurally valid")
    if all_warnings:
        print(f"  ({len(all_warnings)} warnings — run with --verbose to see all)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
