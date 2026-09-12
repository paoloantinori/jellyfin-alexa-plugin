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

WARNING-level checks (never affect the exit code; the CI validate-models job is
advisory and a false positive here must not break it):
  - Bare album carriers: a PlayAlbumIntent sample whose carrier text does not
    name the media noun, in locales whose album slot is an AMAZON.* free-text
    type (CLAUDE.md anti-pattern #11; the catalog-backed AlbumName architecture
    of it-IT is skipped automatically via the slot type, not a locale list).
  - Stale NLU fixtures: a PlayAlbumIntent fixture utterance whose carrier shape
    matches no current sample of that intent in that locale (heuristic; caught
    the JF-459 case where a fixture referenced a deleted sample and only a
    manual profile-nlu probe noticed). Guards the fixtures mirror only; the
    remaining sample mirrors (docs/, docs-site/) stay manual (CLAUDE.md
    anti-pattern #11 lists them all).
  - VOICE_COMMANDS.md row drift (JF-513.1 item 7): every hand-maintained row
    must map to a model intent and list only utterances the model still
    carries, every custom intent with samples must have a row, and every md
    section must correspond to a model file. Partial rows are the table's
    documented convention and are not warned.
  - BrowseCategory id drift (JF-468): every locale must carry the English
    canonical ids artists/albums/songs on the shared concept values, and
    every locale other than it-IT must carry no ids beyond those three.
    it-IT also ids its extra it-IT-only concepts (film, serie, playlist,
    ...), which are deliberately not enumerated here.
  - Template regen equality (JF-316): every locale that has a YAML template
    (templates/<locale>.yaml) must regenerate its committed model JSON
    byte-identically. A mismatch means the committed JSON was hand-edited
    past the template (or the template was edited without regenerating);
    the template is the one writer. Along the same walk, the JellyfinArtist
    static seed (JF-415) must stay identical across the en-* family (it-IT's
    block legitimately differs); that sub-check retires when the generator
    owns the seed from a shared table.

Exit code: 0 if all checks pass, 1 if any error found. Warnings alone exit 0.
--verbose prints every warning instead of the first 20.
"""

import json
import re
import sys
from pathlib import Path

from generate_interaction_model import MODELS_DIR  # the one layout owner

FIXTURES_DIR = Path(__file__).resolve().parent.parent / "tests" / "integration" / "fixtures"

# Slot placeholder span in a sample utterance, e.g. '{album}' in "play the album {album}".
SLOT_PLACEHOLDER_RE = re.compile(r"\{[^}]*\}")

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

# Media nouns a PlayAlbumIntent carrier must name, keyed by language prefix.
# SOURCE OF TRUTH: CLAUDE.md "Interaction Model Anti-Patterns" #11 (bare album
# carriers); if that table changes, change this one in the same commit. The
# truncated stems ('lbum', 'لبوم') intentionally match both plain and
# accented/case variants ('album', 'Album', 'álbum', 'ألبوم') without needing
# unicode accent folding; matching is case-insensitive.
ALBUM_CARRIER_NOUNS: dict[str, list[str]] = {
    "en": ["lbum", "record"],
    "de": ["lbum", "Platte"],
    "es": ["lbum", "disco"],
    "fr": ["lbum", "disque"],
    "pt": ["lbum"],
    "nl": ["lbum"],
    "ar": ["لبوم"],
    "ja": ["アルバム"],
    "hi": ["एल्बम"],
}

# BrowseCategory slot-value id conventions (JF-468). Every locale carries the
# English canonical ids on the three concepts shared across the model family
# (artists/albums/songs); every locale other than it-IT carries ids on
# exactly those three values. it-IT ids its extra it-IT-only concepts too
# (film, serie, playlist, ...), so only the shared three are pinned for it,
# never the extras (they may evolve). The rule is per-locale, NOT keyed on
# the templated/hand-maintained split, which shifts as JF-316 milestones
# land; the constant below is named *_TEMPLATE_LOCALE for historical
# reasons (it-IT was the only templated locale when JF-468 landed) and
# marks the extra-concepts exemption only.
BROWSE_CATEGORY_SHARED_IDS = {"artists", "albums", "songs"}
BROWSE_CATEGORY_TEMPLATE_LOCALE = "it-IT"


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


def intent_by_name(lm: dict, name: str) -> dict | None:
    """Return the intent dict with this name, or None if the locale lacks it."""
    return next((i for i in lm.get("intents", []) if i.get("name") == name), None)


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

    # 10. Bare album carriers (WARNING, CLAUDE.md anti-pattern #11): a
    # PlayAlbumIntent sample whose carrier (placeholders stripped) does not name
    # the media makes PlayAlbumIntent greedily compete with PlaySongIntent on
    # free-text album slots. Only applies when the album slot is an AMAZON.*
    # free-text type (read from the model itself): a catalog-backed custom type
    # such as it-IT's AlbumName constrains matching via the catalog instead, so
    # its carriers are exempt. Locales whose language prefix has no noun table
    # entry (currently 'it') are also skipped: CLAUDE.md #11 defines no nouns
    # for the catalog-backed locale, and inventing them here would drift from
    # the documented source. Placeholders are stripped BEFORE the noun check
    # because '{album}' itself contains the noun and would defeat detection.
    nouns = ALBUM_CARRIER_NOUNS.get(locale.split("-")[0])
    intent = intent_by_name(lm, "PlayAlbumIntent")
    if intent and nouns:
        album_type = next(
            (s.get("type") for s in intent.get("slots", []) if s.get("name") == "album"),
            None,
        )
        if album_type and album_type.startswith("AMAZON."):
            # Same carrier normalization as the fixture lint (_lint_normalize:
            # lowercase + whitespace collapse), so the two checks cannot drift.
            lowered_nouns = [n.lower() for n in nouns]
            for sample in intent.get("samples", []):
                if "{album}" not in sample:
                    continue
                carrier = _lint_normalize(SLOT_PLACEHOLDER_RE.sub("", sample))
                if not any(n in carrier for n in lowered_nouns):
                    warnings.append(
                        f"{prefix} PlayAlbumIntent bare album carrier "
                        f"(CLAUDE.md anti-pattern #11): '{sample}'"
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


# Intents the fixture lint covers. Scoped to the concrete JF-459 case; extend
# only with intents whose fixtures are known to be sample-shaped (the lint is a
# heuristic, not an oracle; see lint_fixture_carriers).
FIXTURE_LINT_INTENTS = {"PlayAlbumIntent"}


def _lint_normalize(text: str) -> str:
    """Lowercase and collapse whitespace so sample/utterance comparison is shape-only."""
    return re.sub(r"\s+", " ", text.lower()).strip()


def _sample_fragments(sample: str) -> list[str]:
    """Literal (non-placeholder) fragments of a sample, in order, normalized."""
    return [_lint_normalize(part) for part in SLOT_PLACEHOLDER_RE.split(sample) if part.strip()]


def _fragments_in_order(text: str, fragments: list[str]) -> bool:
    """True when every fragment appears in text (already _lint_normalize'd), in order.

    Subsequence-of-fragments containment: the sample 'Lis l'album {album} de {musician}'
    requires the literal spans 'lis l'album' and 'de' to appear in the utterance in that
    order (the placeholder spans absorb whatever sits between them). A sample with no
    literal fragments (a bare '{album}') matches every utterance: lenient by design,
    this is a warning heuristic, never a gate.
    """
    pos = 0
    for fragment in fragments:
        idx = text.find(fragment, pos)
        if idx < 0:
            return False
        pos = idx + len(fragment)
    return True


def lint_fixture_carriers(all_models: dict[str, dict], fixtures_dir: Path = FIXTURES_DIR) -> list[str] | None:
    """WARNING lint: NLU fixture utterances that no current sample covers.

    Returns the warning list, or None when the lint did NOT run (PyYAML
    missing, fixtures directory absent), so the caller cannot mistake a
    skip for an all-clear.

    HEURISTIC, NOT AN ORACLE: profile-nlu legitimately routes utterances that are
    not literal samples (NLU generalization), so a warning means "no current
    sample shares this carrier shape", which is a stale-fixture smell rather
    than a broken test, and never a failure. The concrete case (JF-459): trimming bare
    album carriers deleted 'Lis {album} de {musician}' while fixture
    'Lis la musique de the beatles' still expected PlayAlbumIntent; profile-nlu
    kept routing it via other samples, so only a manual probe caught the drift.
    """
    warnings: list[str] = []
    if not fixtures_dir.is_dir():
        print("  SKIP: fixtures directory not found:", fixtures_dir)
        return None
    try:
        import yaml
    except ImportError:
        print("  SKIP: PyYAML not installed, fixture lint not run")
        return None

    for locale, lm in sorted(all_models.items()):
        fixture_path = fixtures_dir / f"{locale}.yaml"
        if not fixture_path.is_file():
            continue
        try:
            data = yaml.safe_load(fixture_path.read_text())
        except yaml.YAMLError as e:
            print(f"  SKIP: [{locale}] fixture is not parseable YAML: {e}")
            continue
        if not isinstance(data, dict):
            print(f"  SKIP: [{locale}] fixture is not a YAML mapping")
            continue
        tests = data.get("tests")
        if not isinstance(tests, list):
            print(f"  SKIP: [{locale}] fixture 'tests' is not a list")
            continue
        # Depends only on the locale's samples, not on the fixture tests: hoist
        # it out of the per-test loop.
        lint_fragments: dict[str, list[list[str]]] = {}
        for intent_name in FIXTURE_LINT_INTENTS:
            intent = intent_by_name(lm, intent_name)
            if intent and intent.get("samples"):
                lint_fragments[intent_name] = [_sample_fragments(s) for s in intent["samples"]]
        for test in tests:
            # Malformed entries must not crash the advisory validator.
            if not isinstance(test, dict):
                continue
            intent_name = test.get("expected_intent")
            if not isinstance(intent_name, str) or not intent_name:
                continue
            fragment_lists = lint_fragments.get(intent_name)
            if not fragment_lists:
                continue
            utterance = test.get("utterance")
            if not isinstance(utterance, str) or not utterance:
                continue
            text = _lint_normalize(utterance)
            if not any(_fragments_in_order(text, fragments) for fragments in fragment_lists):
                warnings.append(
                    f"  [{locale}] fixture utterance '{utterance}' matches no "
                    f"{intent_name} sample carrier (possible stale fixture)"
                )

    return warnings


def lint_browse_category_ids(all_models: dict[str, dict]) -> list[str]:
    """WARNING lint: BrowseCategory slot-value ids drifting from the shared key space.

    The ids are model metadata only (the handler resolves by canonical value
    NAME, never by id), so drift is not an error; but any future id-keyed
    lookup assumes one key space across locales: the English canonical three
    (artists/albums/songs) on the shared concepts, everywhere. Two failure
    shapes warn: a locale missing a shared id, and a locale other than it-IT
    carrying an id beyond the shared three. it-IT's extra concepts are out
    of scope on purpose (they may evolve). The rule is per-locale, not
    per-maintenance-mode: the templated set grows as JF-316 milestones land.
    """
    warnings: list[str] = []
    for locale, lm in sorted(all_models.items()):
        type_def = next(
            (t for t in lm.get("types", []) if isinstance(t, dict) and t.get("name") == "BrowseCategory"),
            None,
        )
        if type_def is None:
            # A missing BrowseCategory type is a cross-locale error elsewhere.
            continue
        ids = {
            v["id"]
            for v in type_def.get("values", [])
            if isinstance(v, dict) and v.get("id")
        }
        missing = BROWSE_CATEGORY_SHARED_IDS - ids
        if missing:
            warnings.append(
                f"  [{locale}] BrowseCategory is missing shared id(s) "
                f"{sorted(missing)} (JF-468 key-space convention)"
            )
        if locale != BROWSE_CATEGORY_TEMPLATE_LOCALE:
            extra = ids - BROWSE_CATEGORY_SHARED_IDS
            if extra:
                warnings.append(
                    f"  [{locale}] BrowseCategory carries id(s) {sorted(extra)} "
                    f"beyond the shared {sorted(BROWSE_CATEGORY_SHARED_IDS)} set"
                )
    return warnings


def _first_divergence(expected: str, actual: str) -> str:
    """First differing line pair between two serialized models, for warnings."""
    exp_lines = expected.splitlines()
    act_lines = actual.splitlines()
    for i in range(max(len(exp_lines), len(act_lines))):
        e = exp_lines[i] if i < len(exp_lines) else "<EOF>"
        a = act_lines[i] if i < len(act_lines) else "<EOF>"
        if e != a:
            return f"line {i + 1}: template regenerates {e.strip()!r}, committed has {a.strip()!r}"
    return "no line difference (trailing newline?)"


def _jellyfin_artist_seed(model: dict) -> str | None:
    """Canonical JSON of the JellyfinArtist type values, for seed equality."""
    for t in model.get("languageModel", {}).get("types", []):
        if isinstance(t, dict) and t.get("name") == "JellyfinArtist":
            return json.dumps(t.get("values", []), ensure_ascii=False)
    return None


def check_template_regen_equality() -> list[str] | None:
    """WARNING check (JF-316): templated locales must regenerate their models.

    For every templates/<locale>.yaml, rebuild the model in memory with
    generate_interaction_model.build_model and the generator's own
    serialize_model (the shared writer: a future serialization change must
    move both sides together, never desync them), and compare against the
    committed model JSON text. A mismatch means the committed JSON drifted
    from the template (hand edit, or a template change without a regen);
    the template is the one writer, so the JSON is build output.

    Along the same walk: the JellyfinArtist static seed (JF-415) must be
    identical across the en-* family members (it-IT's 8-value block
    legitimately differs, so the comparison is en-family-scoped). This
    sub-check retires naturally when the generator owns the seed from a
    shared table.

    Returns the warning list, or None when the check did NOT run (no
    templates found, generate_interaction_model or PyYAML not importable),
    so the caller cannot mistake a skip for an all-clear.

    Warning-level by design: the CI validate-models job is advisory and a
    transitional false positive must not break it. Byte-level here (not
    structural) on purpose: key-order and formatting desync are exactly
    the hand-edit shapes this check exists to catch.
    """
    templates_dir = MODELS_DIR / "templates"
    template_files = sorted(templates_dir.glob("*.yaml")) if templates_dir.is_dir() else []
    if not template_files:
        return None

    try:
        import yaml

        import generate_interaction_model as generator
    except ImportError as e:
        print(f"  SKIP: cannot import generate_interaction_model or PyYAML ({e}); regen check not run")
        return None

    warnings: list[str] = []
    en_artist_seeds: dict[str, str] = {}  # en-* locale -> canonical seed JSON
    for template_path in template_files:
        locale = template_path.stem
        model_path = MODELS_DIR / f"model_{locale}.json"
        if not model_path.is_file():
            warnings.append(
                f"  [{locale}] has template {template_path.name} but no model_{locale}.json"
            )
            continue
        with open(template_path) as f:
            try:
                config = yaml.safe_load(f)
            except yaml.YAMLError as e:
                warnings.append(
                    f"  [{locale}] template {template_path.name} is not parseable "
                    f"YAML: {e}"
                )
                continue
        try:
            model = generator.build_model(config)
            regenerated = generator.serialize_model(model)
        except Exception as e:
            # The generator's template guards (unknown key, bad {ref}, ...)
            # raise ValueError with an authoring message, but a structurally
            # malformed template (top-level list, section-as-list, None
            # config, ...) can raise anything; an advisory check warns
            # instead of crashing the validator.
            warnings.append(
                f"  [{locale}] template {template_path.name} is invalid: "
                f"{type(e).__name__}: {e}"
            )
            continue
        if locale.startswith("en-"):
            seed = _jellyfin_artist_seed(model)
            if seed is not None:
                en_artist_seeds[locale] = seed
        committed = model_path.read_text()
        if regenerated != committed:
            warnings.append(
                f"  [{locale}] committed model_{locale}.json differs from "
                f"templates/{template_path.name} regeneration "
                f"({_first_divergence(regenerated, committed)}); regenerate with "
                f"`python3 scripts/generate_interaction_model.py {locale}`"
            )

    if len(set(en_artist_seeds.values())) > 1:
        reference = sorted(en_artist_seeds)[0]
        for locale in sorted(en_artist_seeds):
            if en_artist_seeds[locale] != en_artist_seeds[reference]:
                warnings.append(
                    f"  [{locale}] JellyfinArtist static seed differs from the "
                    f"en-* family (JF-415: keep the block identical across "
                    "en-US/GB/AU/CA/IN; CatalogSyncTask replaces the whole "
                    "type with the live artist catalog at deploy time)"
                )
    return warnings


VOICE_COMMANDS_PATH = Path(__file__).resolve().parent.parent / "VOICE_COMMANDS.md"


def _voice_commands_sections(
    md_text: str,
) -> tuple[dict[str, list[tuple[str, list[str]]]], list[tuple[str, str]]]:
    """Parse VOICE_COMMANDS.md into ({locale: [(row title, [samples])]}, malformed rows)."""
    sections: dict[str, list[tuple[str, list[str]]]] = {}
    malformed_rows: list[tuple[str, str]] = []
    current: str | None = None
    for line in md_text.splitlines():
        m = re.match(r'### <a id="[a-z-]+"></a>.*\(([a-zA-Z]{2}-[A-Za-z]{2})\)', line)
        if m:
            current = m.group(1)
            if current in sections:
                malformed_rows.append(
                    (current, "duplicate locale heading: earlier rows re-wiped")
                )
            sections[current] = []
            continue
        if line.startswith("|") and "`" in line:
            parts = [p.strip() for p in line.strip().strip("|").split("|")]
            malformed = len(parts) != 2 or not parts[0]
            if malformed:
                # A pipe inside a cell or an odd row shape: name it instead of
                # silently dropping the row from linting.
                target = current if current else "<before any locale heading>"
                malformed_rows.append((target, line.strip()[:60]))
                continue
            if current is None:
                malformed_rows.append(("<before any locale heading>", line.strip()[:60]))
                continue
            cell = parts[1]
            if cell.count("`") % 2:
                malformed_rows.append((current, "unpaired backticks: " + line.strip()[:50]))
                continue
            sections[current].append((parts[0], re.findall(r"`([^`]*)`", cell)))
    return sections, malformed_rows


def _row_intent_candidates(title: str) -> list[str]:
    pascal = "".join(w.capitalize() for w in title.split())
    return [pascal + "Intent", "AMAZON." + pascal + "Intent"]


def lint_voice_commands_rows(
    all_models: dict[str, dict], md_path: Path = VOICE_COMMANDS_PATH
) -> list[str] | None:
    """WARNING lint: VOICE_COMMANDS.md rows drifting from the models (JF-513.1 item 7).

    The hand-maintained utterance table went stale twice (PR #15 orphaned the
    English rows; JF-459 orphaned 11 more; JF-475 documented a phantom row) and
    only the PlayAlbum-specific warning check #10 guards any of it. The warning
    shapes, per locale:
    - a row lists an utterance the model no longer carries (stale mirror);
    - a custom intent with samples has no row at all (coverage gap, the JF-494
      class);
    - a row title maps to no intent in the model (renamed intent or typo);
    - a row survives an intent whose samples list has emptied (retirement);
    - a row or heading shape the parser cannot attribute (pipes inside cells,
      unpaired backticks, rows before the first heading, duplicate headings).
    Deliberately NOT warned: partial rows (the table lists a curated selection
    of each intent's samples, per its own header) and count mismatches.
    The row-title convention is PascalCase words plus "Intent" (the reverse of
    camelCase splitting), a second convention parallel to the generator's
    GROUPS labels; a title that stops mapping warns loudly rather than
    silently.

    Returns None when the markdown file is absent.
    """
    if not md_path.exists():
        return None
    md_text = md_path.read_text()
    sections, malformed_rows = _voice_commands_sections(md_text)
    warnings: list[str] = []
    for where, note in malformed_rows:
        warnings.append(f"  [{where}] unparseable VOICE_COMMANDS row: {note}")
    for locale in sorted(set(sections) - set(all_models)):
        warnings.append(
            f"  [{locale}] VOICE_COMMANDS.md section has no model file (phantom locale)"
        )
    for locale, lm in sorted(all_models.items()):
        by_name = {
            i.get("name"): i.get("samples", [])
            for i in lm["intents"]
            if i.get("name")
        }
        rows = sections.get(locale)
        if rows is None:
            warnings.append(
                f"  [{locale}] no VOICE_COMMANDS.md section (missing mirror section)"
            )
            continue
        for title, samples in rows:
            candidates = [c for c in _row_intent_candidates(title) if c in by_name]
            if not candidates:
                warnings.append(
                    f"  [{locale}] VOICE_COMMANDS row '{title}' maps to no model "
                    f"intent (renamed intent or typo'd title)"
                )
                continue
            name = candidates[0]
            model_samples = by_name[name]
            if samples and not model_samples:
                warnings.append(
                    f"  [{locale}] '{title}' row lists utterances but intent "
                    f"{name} carries no samples anymore (retired intent?)"
                )
                continue
            stale = [s for s in samples if s not in model_samples]
            if stale:
                warnings.append(
                    f"  [{locale}] '{title}' row lists utterances absent from the "
                    f"model: {stale[0]}"
                    + (f" (+{len(stale) - 1} more)" if len(stale) > 1 else "")
                )
        row_titles = [t for t, _ in rows]
        for name, model_samples in by_name.items():
            if name.startswith("AMAZON.") or not model_samples:
                continue
            if not any(name in _row_intent_candidates(t) for t in row_titles):
                warnings.append(
                    f"  [{locale}] intent {name} has {len(model_samples)} samples "
                    f"but no VOICE_COMMANDS row"
                )
    return warnings


def main() -> int:
    verbose = "--verbose" in sys.argv[1:]
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
        if verbose:
            for w in warnings:
                print(f"  WARN: {w}")

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

    # Phase 3: NLU fixture carrier lint (heuristic warning check)
    if all_models:
        print("\nNLU fixture carrier lint:")
        fixture_warnings = lint_fixture_carriers(all_models)
        if fixture_warnings is None:
            pass  # skipped: the SKIP line above says why, never print all-clear
        else:
            all_warnings.extend(fixture_warnings)
            if fixture_warnings:
                for w in fixture_warnings:
                    print(f"  WARN: {w}")
            else:
                print("  All linted fixture utterances match a current sample carrier")

    # Phase 4: BrowseCategory id-parity lint (JF-468 warning check)
    if all_models:
        print("\nBrowseCategory id lint:")
        id_warnings = lint_browse_category_ids(all_models)
        all_warnings.extend(id_warnings)
        if id_warnings:
            for w in id_warnings:
                print(f"  WARN: {w}")
        else:
            print("  All locales carry the shared English ids on the BrowseCategory concepts")

    # Phase 5: template regen-equality check (JF-316 warning check)
    print("\nTemplate regen equality:")
    regen_warnings = check_template_regen_equality()
    if regen_warnings is None:
        pass  # skipped: the SKIP line above says why, never print all-clear
    else:
        all_warnings.extend(regen_warnings)
        if regen_warnings:
            for w in regen_warnings:
                print(f"  WARN: {w}")
        else:
            print("  Every templated locale regenerates its committed model byte-identically")

    # Phase 6: VOICE_COMMANDS.md row-vs-model lint (JF-513.1 item 7 warning check)
    if all_models:
        print("\nVOICE_COMMANDS row lint:")
        vc_warnings = lint_voice_commands_rows(all_models)
        if vc_warnings is None:
            pass  # file absent: skip silently
        else:
            all_warnings.extend(vc_warnings)
            if vc_warnings:
                for w in vc_warnings:
                    print(f"  WARN: {w}")
            else:
                print("  Every row maps to a model intent and lists only live utterances")

    # Summary
    print(f"\n{'='*60}")
    if all_warnings:
        print(f"WARN: {len(all_warnings)} warning(s) (non-blocking):")
        shown = all_warnings if verbose else all_warnings[:20]
        for w in shown:
            print(w)
        if not verbose and len(all_warnings) > 20:
            print(f"  ... and {len(all_warnings) - 20} more warnings")

    if all_errors:
        print(f"FAIL: {len(all_errors)} error(s) found:")
        for e in all_errors:
            print(e)
        return 1

    print("PASS: All interaction models are structurally valid")
    if all_warnings:
        hint = "" if verbose else "; run with --verbose to see all"
        print(f"  ({len(all_warnings)} warnings{hint})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
