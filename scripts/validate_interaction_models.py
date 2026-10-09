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
  - CJK slot-glue: a slot ref whose brace sits directly against a CJK
    character (Hiragana, Katakana, CJK punctuation, ideographs incl.
    Extension A, halfwidth katakana, fullwidth forms) with no ASCII
    space; SMAPI rejects it at build time (JF-638, after the three
    live incidents JF-513, JF-326, JF-636). The halfwidth/fullwidth
    ranges are also banned anywhere in a sample, glue-adjacent or
    not (JF-644)

WARNING-level checks (never affect the exit code; error-level checks do, and the
CI validate-models job has been blocking on errors since JF-556):
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
  - es trio PlayEpisodeIntent mirroring (JF-844.1): the es locales are
    verbatim-transcription templates with no shared-include mechanism, so the
    es-MX / es-US PlayEpisode sample sets are hand-mirrored subsets of es-ES's.
    The lint warns when a sibling carries a row es-ES lacks, when the
    es-ES-only surplus stops matching the divergence whitelist exactly (an
    unlisted surplus row is unguarded single-locale drift; a listed row that is
    no longer a divergence means the whitelist rotted), and when an es locale
    present in the models has no sibling-table row (the JF-824 self-reminding
    coverage finding).
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
from collections.abc import Callable

import yaml
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

# CJK text glued to a slot ref is an SMAPI build failure (JF-638; live
# InvalidCharInSamples/InvalidSample hits: JF-513 {musician}を再生, JF-326
# 星{star_rating}で, JF-636 速度{speed}). The documented rule
# (templates/ja-JP.yaml header) allows ONLY a regular ASCII space between a
# slot and adjacent CJK text, so there is no legitimate glued form to exempt.
# U+3000 (ideographic space) sits inside the span on purpose: the same header
# bans it in samples entirely, so a U+3000 "separator" must flag, not pass.
# JF-644 widens the class with the Halfwidth/Fullwidth Forms tail and lowers
# the ideograph floor to Extension A (U+3400), staying contiguous.
_FULLWIDTH_FORMS = (0xFF01, 0xFF5E)  # fullwidth ASCII variants
_HALFWIDTH_KATAKANA = (0xFF66, 0xFF9D)  # halfwidth katakana


def _is_banned_ff(ch: str) -> bool:
    # Both spans the ja-JP template header bans from samples outright
    # (InvalidCharInSamples, live-verified on U+FF1F).
    o = ord(ch)
    return (
        _FULLWIDTH_FORMS[0] <= o <= _FULLWIDTH_FORMS[1]
        or _HALFWIDTH_KATAKANA[0] <= o <= _HALFWIDTH_KATAKANA[1]
    )


def _is_cjk(ch: str) -> bool:
    # One contiguous span covers CJK punctuation (U+3000-U+303F, including
    # U+3000), Hiragana (U+3040-U+309F), and Katakana (U+30A0-U+30FF, incl.
    # ー U+30FC); the second covers CJK Unified Ideographs including
    # Extension A (U+3400-U+4DBF) through U+9FFF; the tail joins
    # _is_banned_ff, whose characters are banned anyway, so a glue-adjacent
    # occurrence is certainly wrong.
    o = ord(ch)
    return 0x3000 <= o <= 0x30FF or 0x3400 <= o <= 0x9FFF or _is_banned_ff(ch)


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

        # 6. AMAZON.SearchQuery coexistence violation (JF-557 refinement: SAMPLE-level
        # error, intent-level warning). SMAPI's enforced constraint is that no single
        # utterance combines SearchQuery with another slot; the intent-level
        # prohibition this check used to enforce is NOT what SMAPI rejects today -
        # live 2026-09-13: BrowseLibraryIntent carries filter (SearchQuery) +
        # browse_category in all 17 locales and every build SUCCEEDED (JF-550/JF-557
        # batches). The historical 9+ incidents were sample-level combinations.
        # The intent-level shape still warns: it is unusual and worth a look.
        if has_search_query and other_slots:
            warnings.append(
                f"{prefix} Intent '{name}': AMAZON.SearchQuery coexists with other "
                f"slots ({other_slots}) at INTENT level; legal only while no single "
                f"sample combines them (check below enforces that)"
            )
        if has_search_query:
            sq_slots = {
                s["name"]
                for s in slots
                if isinstance(s, dict) and s.get("type") == "AMAZON.SearchQuery"
            }
            for sample in intent.get("samples", []):
                placeholders = {
                    p.strip("{}") for p in SLOT_PLACEHOLDER_RE.findall(sample)
                }
                if len(placeholders) > 1 and placeholders & sq_slots:
                    errors.append(
                        f"{prefix} Intent '{name}': sample '{sample}' combines "
                        f"AMAZON.SearchQuery with another slot (SMAPI rejects the build)"
                    )
                    break

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

    # 10a. Orphan dialog prompts (WARNING, JF-542): a prompt definition no
    # dialog slot references is inert on SMAPI (Alexa ignores it) and the
    # code-driven ElicitSlot flow reads ResponseStrings text, never these
    # ids - dead weight and a cross-locale shape drift.
    prompt_ids = [p.get("id") for p in (lm.get("prompts") or [])]
    if prompt_ids:
        referenced = set()
        for dialog_intent in lm.get("dialog", {}).get("intents", []):
            for slot in dialog_intent.get("slots", []):
                elicitation = (slot.get("prompts") or {}).get("elicitation")
                if elicitation:
                    referenced.add(elicitation)
        for pid in prompt_ids:
            if pid not in referenced:
                warnings.append(
                    f"{prefix} orphan dialog prompt '{pid}' (no slot references it; JF-542)"
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

    # 11. CJK slot-glue (ERROR, JF-638): a slot ref whose brace sits directly
    # against a CJK character passes this validator but SMAPI rejects it at
    # model-build time (InvalidCharInSamples; three live incidents: JF-513,
    # JF-326, JF-636, each found only on the box). The trigger class also
    # covers halfwidth katakana and fullwidth forms (JF-644): they are banned
    # from samples outright, so a glue-adjacent occurrence is certainly
    # wrong. Scanned in EVERY locale, not behind a ja-JP gate: the other 16
    # locales carry no CJK characters in samples at all, so the scan is a
    # no-op there while a locale list would only rot.
    for intent in intents:
        iname = intent.get("name", "<unnamed>")
        for sample in intent.get("samples", []):
            for m in SLOT_PLACEHOLDER_RE.finditer(sample):
                before = sample[m.start() - 1] if m.start() > 0 else ""
                after = sample[m.end()] if m.end() < len(sample) else ""
                sides = []
                if before and _is_cjk(before):
                    sides.append(f"U+{ord(before):04X} directly before '{{'")
                if after and _is_cjk(after):
                    sides.append(f"U+{ord(after):04X} directly after '}}'")
                if sides:
                    errors.append(
                        f"{prefix} Intent '{iname}': sample '{sample}' glues a slot "
                        f"to CJK text ({', '.join(sides)}); separate with a regular "
                        f"ASCII space (SMAPI rejects the build; JF-638)"
                    )
            # Anywhere-level scan (JF-644): the two Halfwidth/Fullwidth ranges
            # fail wherever they appear in a sample, adjacency aside. Same
            # no-locale-gate argument as above: no committed sample in ANY
            # locale carries one, so the scan is a no-op on the real tree.
            banned = sorted({f"U+{ord(ch):04X}" for ch in sample if _is_banned_ff(ch)})
            if banned:
                errors.append(
                    f"{prefix} Intent '{iname}': sample '{sample}' carries "
                    f"fullwidth/halfwidth characters ({', '.join(banned)}); rewrite "
                    f"with standard-width text (banned from samples outright; JF-644)"
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
            # Malformed entries must not crash the validator (warning-level check).
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


# JF-549: PlayEpisodeIntent one-shot carrier prefixes per locale.
# A locale is listed here only when its model ALREADY carries a one-shot carrier
# family; locales without one (pt-BR, nl-NL, ar-SA, hi-IN, ja-JP) are out of
# scope - JF-551 probed all of them 2026-10-04: their wrapper payloads select
# PlayEpisodeIntent on the existing bare/imperative families when the series IS
# in the user's catalog, and every remaining misroute (pt-BR's PlayNext/PlayVideo
# steals, the hi series-truncation, the nl season '?') is the catalog
# ER_SUCCESS_NO_MATCH confidence penalty that hits any NON-library series value
# (the JF-684 class; evidence and per-locale dispositions in the JF-551 notes),
# EXCEPT the ja 見たい steal, which is catalog-independent and tracked as
# JF-761. The es-* entries are the JF-551 SUBJUNCTIVE family: the es one-shot
# wrapper ("pide a {inv} que ...") presents the subjunctive, not the infinitive
# (the 2026-09-13 infinitive attempt was wrong morphology and reverted).
PLAY_EPISODE_ONESHOT_PREFIXES = {
    "it-IT": "Di ",
    "de-DE": "Zu ",
    "fr-FR": "De ",
    "fr-CA": "De ",
    "en-US": "to ",
    "en-GB": "to ",
    "en-AU": "to ",
    "en-CA": "to ",
    "en-IN": "to ",
    "es-ES": "reproduzca ",
    "es-MX": "reproduzca ",
    "es-US": "reproduzca ",
}


def _series_first(sample: str) -> bool:
    """Whether the sample places {series_name} before both number slots."""
    i = sample.find("{series_name}")
    if i < 0:
        return False
    return all(
        sample.find(t) > i
        for t in ("{season_number}", "{episode_number}")
        if t in sample
    )


def lint_play_episode_one_shot_order(all_models: dict[str, dict]) -> list[str]:
    """WARNING lint: PlayEpisodeIntent one-shot samples must exist in BOTH word orders.

    JF-549 (live 2026-09-12): the one-shot wrapper presents the infinitive with the
    series up front, and a locale whose one-shot samples exist only in the
    series-LAST order fills season/episode but drops series_name. Every locale that
    carries a one-shot carrier family at all must have a series-first one too.
    """
    warnings: list[str] = []
    for locale, lm in sorted(all_models.items()):
        intent = intent_by_name(lm, "PlayEpisodeIntent")
        if intent is None:
            continue  # a missing intent is a cross-locale error elsewhere
        prefix = PLAY_EPISODE_ONESHOT_PREFIXES.get(locale)
        if not prefix:
            continue
        one_shot = [s for s in intent.get("samples", []) if s.startswith(prefix)]
        if not any(_series_first(s) for s in one_shot):
            state = (
                f"has no one-shot samples at all (expected carriers starting with '{prefix}')"
                if not one_shot
                else "one-shot samples exist only in the series-LAST order"
            )
            warnings.append(
                f"  [{locale}] PlayEpisodeIntent {state}; the one-shot wrapper "
                f"presents the series FIRST and the slot is dropped (JF-549)"
            )
    return warnings



# JF-844.1: the es trio PlayEpisodeIntent mirroring whitelist. The three es
# locales are verbatim-transcription templates with no shared-include mechanism
# (the JF-316 golden-master decision), so the PlayEpisodeIntent rows are
# hand-mirrored across templates/es-ES.yaml, es-MX.yaml and es-US.yaml. Per
# sibling locale, these are the ONLY permitted divergences: rows es-ES carries
# that the sibling does not. The rows are sourced verbatim from the committed
# models (json diff, not invented): the three 'que reproduzca' connector-residue
# subjunctive twins landed es-ES-only in 22fed1ab (JF-844) pending the es-ES
# connector probe going green; the siblings inherit them after es-ES is solved,
# and that inheritance is exactly the change that empties these rows. Any other
# single-locale edit (the drift class this lint exists for) fires until its row
# lands here with provenance, so the whitelist cannot rot silently.
ES_TRIO_CONNECTOR_TWINS = frozenset(
    {
        "que reproduzca {series_name} temporada {season_number} episodio {episode_number}",
        "que reproduzca la temporada {season_number} episodio {episode_number} de {series_name}",
        "que reproduzca el episodio {episode_number} de {series_name}",
    }
)

# Per sibling: the day one sibling inherits a connector row ahead of the other
# is a one-key edit here, never a mechanism change.
ES_TRIO_PLAY_EPISODE_DIVERGENCES: dict[str, frozenset[str]] = {
    "es-MX": ES_TRIO_CONNECTOR_TWINS,
    "es-US": ES_TRIO_CONNECTOR_TWINS,
}

ES_TRIO_REFERENCE_LOCALE = "es-ES"


def lint_es_trio_play_episode_mirroring(all_models: dict[str, dict]) -> list[str] | None:
    """WARNING lint (JF-844.1): the es trio PlayEpisodeIntent sample mirroring.

    es-MX and es-US must be subsets of es-ES, and the es-ES-only surplus per
    sibling must equal ES_TRIO_PLAY_EPISODE_DIVERGENCES exactly. Three failure
    shapes warn: a sibling row missing from es-ES (the mirroring broke in the
    sibling), an es-ES-only row absent from the whitelist (unguarded
    single-locale drift in the reference, the class commit 22fed1ab performed
    deliberately for the connector rows), and a whitelisted row that is no
    longer a divergence (the row was mirrored or deleted; the data is stale).
    Warning-level per the check #10 / JF-824 convention: a deliberate trio
    change fires until the whitelist row updates in the same change, so the
    lint surfaces drift without ever breaking CI.

    Returns the warning list, or None when the lint did NOT run (a trio model
    or its PlayEpisodeIntent is absent), so the caller cannot mistake a skip
    for an all-clear. The absence itself is only warning-covered elsewhere (a
    missing PlayEpisodeIntent warns in the cross-locale check; a missing model
    file is silent beyond this lint's own SKIP line), so the SKIP must print,
    never return quietly.
    """
    intents: dict[str, set[str]] = {}
    for locale in (ES_TRIO_REFERENCE_LOCALE, *ES_TRIO_PLAY_EPISODE_DIVERGENCES):
        lm = all_models.get(locale)
        if lm is None:
            print(f"  SKIP: [{locale}] model absent, es trio mirroring lint not run")
            return None
        intent = intent_by_name(lm, "PlayEpisodeIntent")
        if intent is None:
            print(f"  SKIP: [{locale}] has no PlayEpisodeIntent, es trio mirroring lint not run")
            return None
        intents[locale] = set(intent.get("samples", []))

    reference = intents[ES_TRIO_REFERENCE_LOCALE]
    warnings: list[str] = []
    # JF-824 convention: an es locale present in the models but absent from
    # the sibling table is silently outside the lint; fire so a new es locale
    # gets its row (or an explicit exclusion) in the same change that adds it.
    for locale in sorted(
        l
        for l in all_models
        if l.split("-")[0] == "es"
        and l != ES_TRIO_REFERENCE_LOCALE
        and l not in ES_TRIO_PLAY_EPISODE_DIVERGENCES
    ):
        warnings.append(
            f"  [{locale}] es locale present but no ES_TRIO_PLAY_EPISODE_DIVERGENCES "
            "row; the PlayEpisode mirroring lint does not cover it "
            "(add one in the same change)"
        )
    for sibling, expected_divergences in ES_TRIO_PLAY_EPISODE_DIVERGENCES.items():
        sibling_samples = intents[sibling]
        for sample in sorted(sibling_samples - reference):
            warnings.append(
                f"  [{sibling}] PlayEpisodeIntent sample '{sample}' is not in "
                f"{ES_TRIO_REFERENCE_LOCALE}; the es trio rows are hand-mirrored, "
                f"mirror it in templates/{ES_TRIO_REFERENCE_LOCALE}.yaml (JF-844.1)"
            )
        surplus = reference - sibling_samples
        for sample in sorted(surplus - expected_divergences):
            warnings.append(
                f"  [{sibling}] {ES_TRIO_REFERENCE_LOCALE}-only PlayEpisodeIntent sample "
                f"'{sample}' is not in the divergence whitelist; mirror the row to "
                f"templates/{sibling}.yaml or record it in ES_TRIO_PLAY_EPISODE_DIVERGENCES "
                f"(JF-844.1)"
            )
        for sample in sorted(expected_divergences - surplus):
            warnings.append(
                f"  [{sibling}] whitelisted PlayEpisodeIntent divergence '{sample}' is "
                f"no longer {ES_TRIO_REFERENCE_LOCALE}-only (mirrored or deleted); update "
                f"ES_TRIO_PLAY_EPISODE_DIVERGENCES (JF-844.1)"
            )
    return warnings


def check_elicit_dialog_registration(
    sources: dict[str, str] | None = None,
) -> tuple[list[str], list[str]]:
    """ERROR check (JF-613 redesign): declaration against declaration.

    The OLD check hand-parsed four C# call shapes (inline arrays, constant-token
    arrays, wrappers, hoisted locals) - every new C# idiom was a fresh
    false-negative escape. The NEW contract: every elicit call site passes
    Util.ElicitSlots.For(<intent>) as its allSlotNames, and the ONE ElicitSlots
    table is compared against every locale's dialog.intents declaration. What
    this checks:

    1. CANONICAL SHAPE: any elicit builder call (BuildDialogElicitResponse /
       BuildElicitSlotResponse / ElicitSlotDirective) outside BaseHandler.cs
       whose span carries no ElicitSlots.For( is an error - the call site chose
       a shape the table cannot guarantee, the exact silent-escape class this
       redesign retires. BaseHandler.cs is exempt: it DEFINES the builders and
       delegates their params internally.
    2. TABLE PARITY: each table entry's slot set must equal every locale's
       dialog.intents entry (and the languageModel slots, via the dialog-vs-lm
       loop that stays). Drift in either direction fails here.
    3. TABLE COMPLETENESS: every intent an elicit call targets must have a
       table entry (a new elicit flow that forgets the table fails).
    4. REGISTRATION (JF-550): every elicited intent must appear in every
       locale's dialog.intents (anti-pattern #9, unchanged).

    ``sources`` injects {filename: content} for the pytest harness; the default
    reads the repo tree ONCE (the JF-612 double read is gone).
    """
    import glob as _glob
    import os as _os
    import re as _re

    repo_root = Path(__file__).resolve().parent.parent
    if sources is None:
        handler_files = [
            p
            for p in _glob.glob(
                str(repo_root / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "**" / "*.cs"),
                recursive=True,
            )
            if f"{_os.sep}bin{_os.sep}" not in p and f"{_os.sep}obj{_os.sep}" not in p
        ]
        sources = {p: Path(p).read_text(encoding="utf-8") for p in handler_files}

    errors: list[str] = []
    warnings: list[str] = []

    intent_names_src = sources.get(
        str(repo_root / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "IntentNames.cs"),
        (repo_root / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "IntentNames.cs").read_text(encoding="utf-8"),
    )
    intent_constants = dict(
        _re.findall(r'public const string (\w+) = "([\w.]+)"', intent_names_src)
    )
    slots_class = _re.search(r"public static class Slots.*?\{(.*?)\}", intent_names_src, _re.S)
    if slots_class is None:
        errors.append("  [code] IntentNames.Slots class not found; the ElicitSlots table cannot be fully parsed (JF-613)")
        slot_constants: dict[str, str] = {}
    else:
        slot_constants = dict(_re.findall(r'public const string (\w+) = "(\w+)"', slots_class.group(1)))

    # --- Parse the ONE ElicitSlots table (declaration source of truth) ---
    elicit_slots_key = next((k for k in sources if k.endswith("ElicitSlots.cs")), None)
    elicit_slots_src = sources[elicit_slots_key] if elicit_slots_key else ""
    table: dict[str, set[str]] = {}
    if not elicit_slots_src:
        errors.append("  [code] Util/ElicitSlots.cs not found; the elicit slot-set table is the parity source of truth (JF-613)")
    else:
        src_nc = _re.sub(r"//[^\n]*", " ", elicit_slots_src)
        for intent_tok, body in _re.findall(r"\[IntentNames\.(\w+)\]\s*=\s*new\[\]\s*\{([^}]*)\}", src_nc):
            slots: set[str] = set()
            for lit in _re.findall(r'"(\w+)"', body):
                slots.add(lit)
            for slot_tok in _re.findall(r"IntentNames\.Slots\.(\w+)", body):
                if slot_tok in slot_constants:
                    slots.add(slot_constants[slot_tok])
            raw_values = _re.findall(r'"(\w+)"|IntentNames\.Slots\.(\w+)', body)
            if len(raw_values) != len(slots):
                errors.append(
                    f"  [code] ElicitSlots row for {intent_constants.get(intent_tok, intent_tok)} has duplicate or unresolvable slot values; "
                    f"duplicates crash ElicitSlotDirective's ToDictionary at runtime, and unresolvable tokens mean IntentNames.Slots drifted"
                )
            resolved = intent_constants.get(intent_tok, intent_tok + "?")
            table[resolved] = slots
        if not table:
            errors.append("  [code] the ElicitSlots table parsed to zero entries; the initializer shape must stay '[IntentNames.X] = new[] { ... }'")

    # --- Elicited targets + canonical-shape scan (per file, comments stripped) ---
    targets: set[str] = set()
    # Non-greedy span to the first ');' can truncate when an ARGUMENT contains
    # ');' (a lambda body, a string literal); extend the span to the statement
    # end heuristically: the first ');' followed by end-of-line (the closing
    # paren of a statement sits at a line end in this codebase's style).
    builder_re = _re.compile(
        r"(BuildDialogElicitResponse|BuildElicitSlotResponse|ElicitSlotDirective)\((.*?)\);(?=\s*(?:$|[;)}]|return))",
        _re.S | _re.M,
    )
    for fname, raw in sources.items():
        src_nc = _re.sub(r"/\*.*?\*/", " ", raw, flags=_re.S)
        src_nc = _re.sub(r"//[^\n]*", " ", src_nc)
        # Path-based, not basename: a future same-named file elsewhere must not
        # silently lose canonical-shape enforcement.
        is_builder_home = str(Path(fname)).replace("\\", "/").endswith(
            ("Alexa/Handler/BaseHandler.cs", "Alexa/Directive/ElicitSlotDirective.cs")
        )
        for builder, span in builder_re.findall(src_nc):
            for token in _re.findall(r"IntentNames\.(\w+)", span):
                resolved = intent_constants.get(token)
                if resolved:
                    targets.add(resolved)
            if is_builder_home:
                continue
            if "ElicitSlots.For(" not in span:
                errors.append(
                    f"  [code] {Path(fname).name}: a {builder} call does not pass ElicitSlots.For(...) "
                    f"as allSlotNames (JF-613 canonical shape; hand-built slot arrays can drift silently)"
                )

    # --- Table completeness: every elicited target needs an entry ---
    for intent in sorted(targets):
        if intent not in table:
            errors.append(
                f"  [code] intent {intent} is elicited but has no ElicitSlots table entry; add it to Util/ElicitSlots.cs AND the 17 templates' dialog sections in the same change"
            )

    # --- Reverse completeness (JF-613 simplify round): a table entry no call
    # site elicits is dead weight that silently drifts. WARNING, not error:
    # removing the entry cascades into the 17 templates' dialog sections, a
    # model change judged out of proportion for harmless weight.
    for intent in sorted(set(table) - targets):
        warnings.append(
            f"  [code] ElicitSlots entry for {intent} is never elicited by any call site; remove it or wire the elicit (its parity is otherwise unchecked)"
        )

    # --- Table parity vs every locale's dialog section + registration ---
    # all_models carries languageModels only; the dialog section is its sibling
    # in the envelope, so re-read the raw files for this part.
    envelope: dict[str, dict] = {}
    for model_path in sorted(MODELS_DIR.glob("model_*.json")):
        with open(model_path, encoding="utf-8") as fh:
            envelope[model_path.name[6:-5]] = json.load(fh)

    for locale, doc in sorted(envelope.items()):
        lm = doc.get("interactionModel", doc).get("languageModel", doc.get("languageModel"))
        dialog = {i.get("name"): i for i in doc.get("interactionModel", doc).get("dialog", {}).get("intents", [])}
        lm_slots = {i["name"]: {s["name"] for s in (i.get("slots") or [])} for i in lm["intents"]}
        for intent in sorted(targets):
            entry = dialog.get(intent)
            if entry is None:
                errors.append(
                    f"  [{locale}] intent {intent} is elicited by handler code but missing from dialog.intents (Amazon silently drops the directive; anti-pattern #9)"
                )
                continue
            dlg_slots = {s.get("name") for s in (entry.get("slots") or [])}
            if intent in table and dlg_slots != table[intent]:
                errors.append(
                    f"  [{locale}] dialog entry for {intent} lists slots {sorted(dlg_slots)} but the ElicitSlots table declares {sorted(table[intent])} (declaration drift; keep the table and the 17 templates in lockstep)"
                )
            if intent in lm_slots and dlg_slots != lm_slots[intent]:
                errors.append(
                    f"  [{locale}] dialog entry for {intent} lists slots {sorted(dlg_slots)} but the languageModel intent declares {sorted(lm_slots[intent])} (MismatchedSlotType at build)"
                )
    return errors, warnings

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

    Warning-level by design: warnings never affect the exit code, so a
    transitional false positive cannot break the error-gated CI job (JF-556).
    Byte-level here (not
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
            # config, ...) can raise anything; a warning-level check warns
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

    The utterance table's locale sections are EMITTED by
    scripts/generate_voice_reference.py (JF-548) and byte-checked by its --check,
    so this lint's live signal is the title-mapping shape (below): it maps a row
    title back to an intent INDEPENDENTLY of the generator's forward mapping, so
    it catches a generate_voice_reference.py title regression that byte-equality
    --check is blind to. Keep _row_intent_candidates the exact inverse of the
    generator's intent_display_title. The remaining shapes can only fire on a
    hand-edited table or a generator bug that --check misses, and stay as cheap
    tripwires. (The table went stale twice in its hand-maintained era: PR #15
    orphaned the English rows, JF-459 eleven more, JF-475 a phantom row.)
    - a row lists an utterance the model no longer carries (stale mirror);
    - a custom intent with samples has no row at all (coverage gap, the JF-494
      class);
    - a row title maps to no intent in the model (renamed intent or typo);
    - a row survives an intent whose samples list has emptied (retirement);
    - a row or heading shape the parser cannot attribute (pipes inside cells,
      unpaired backticks, rows before the first heading, duplicate headings).
    Deliberately NOT warned: partial rows (the table shows a capped, diverse
    selection per its own header; the complete lists live in the by-locale
    reference) and count mismatches.
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


# One-shot wrapper coverage (JF-614, the meta-bug check): the primary one-shot
# construction is "<invocation> <wrapper-marker> <noun> <name>" (it-IT "chiedi a
# mia collezione DI riprodurre il podcast X"). An intent whose sample family
# lacks a wrapper-marker twin NO_SELECTIONs at NLU with zero server-side logs
# (live 2026-09-21: three device beeps on PlayPodcastIntent, JF-551's episode
# sibling). Marker tokens per language prefix, matched case-insensitively inside
# a sample; triage gaps as JF-551-class extensions or accepted.

def _it_markers() -> list[str]:
    """JF-615: the it bare-infinitive markers DERIVED from the template's own
    vocabulary.infinitive list (entry minus the 'Di ' prefix, lowercased,
    trailing space - the convention the hand list used), so the marker set can
    never drift from the vocabulary again (the JF-549 F3 residual: the hand list
    once carried a dead verb with zero referent samples and missed a vocabulary
    verb in the same commit). The Di- stems stay hand-listed (deliberate
    truncations), plus the two live-probed exceptions the vocabulary cannot
    express: 'aggiungere ' (the trainer does NOT generalize aggiungi->
    aggiungere, live FallbackIntent 2026-09-22; the twin is load-bearing) and
    'leggere ' (JF-551's PlayBook family, not in the core verb vocabulary)."""
    di_stems = ["di riprodu", "di suona", "di metti", "di ascolta", "di pleia", "di fammi"]
    derived: list[str] = []
    template = MODELS_DIR / "templates" / "it-IT.yaml"
    try:
        with open(template) as f:
            vocab = (yaml.safe_load(f) or {}).get("vocabulary") or {}
        for entry in vocab.get("infinitive") or []:
            bare = entry.strip()
            if bare.lower().startswith("di "):
                bare = bare[3:]
            marker = bare.lower().strip() + " "
            if marker.strip() and marker not in derived:
                derived.append(marker)
    except Exception as e:  # noqa: BLE001 - degrade loudly, never block the check
        print(f"  WARNING: could not derive it markers from template ({e}); "
              "falling back to the hand list")
        return di_stems + ["riprodurre ", "suonare ", "mettere ", "ascoltare ",
                           "aggiungere ", "leggere "]
    return di_stems + derived + ["aggiungere ", "leggere "]


WRAPPER_MARKERS: dict[str, list[str]] = {
    "it": _it_markers(),
    "en": ["to play", "to listen", "to hear", "to watch", "to stream", "to queue", "to give"],
    "de": ["abspielen", "wiedergeben", "hören", "anschauen"],
    # es: "reproduzca" joined JF-551 (the subjunctive is the real es wrapper
    # morphology behind "pide a X que ..."; the infinitive marker stays for
    # any locale that ever carries an infinitive twin).
    "es": ["reproducir", "reproduzca", "escuchar", "ver ", "poner"],
    "fr": ["écouter", "lire ", "regarder", "mettre"],
    "pt": ["tocar", "ouvir", "assistir", "colocar"],
    "nl": ["afspelen", "luisteren", "kijken"],
    "ar": ["تشغيل", "الاستماع"],
    "hi": ["चलाओ", "सुनो", "दिखाओ"],
    "ja": ["を再生して", "を聴いて", "を見せて"],
}


def check_wrapper_coverage(all_models: dict[str, dict]) -> list[str]:
    """WARNING check: every playable or additive custom intent must carry a
    one-shot wrapper twin in every locale. See WRAPPER_MARKERS above for the
    failure being caught. The Add* family joined 2026-09-22 (review finding):
    the trainer does not generalize aggiungi->aggiungere (verb-level, not
    intent-level), so add intents need explicit infinitive twins exactly like
    play intents, and the marker check must actually examine them."""
    warnings: list[str] = []
    wrapper_families = ("Play", "Add")
    for locale, lm in sorted(all_models.items()):
        prefix = locale.split("-")[0]
        markers = WRAPPER_MARKERS.get(prefix)
        if not markers:
            continue  # locale without a known wrapper construction
        for intent in lm.get("intents", []):
            name = intent.get("name", "")
            if not name.startswith(wrapper_families):
                continue  # the wrapper is a play/add-shape construction
            samples = intent.get("samples", [])
            if not samples:
                continue  # zero-sample intents are another check's job
            lowered = [s.lower() for s in samples]
            if not any(m in s for m in markers for s in lowered):
                warnings.append(
                    f"[{locale}] Intent '{name}' has no one-shot wrapper twin "
                    f"(no sample contains any of {markers}); the '<invocation> <wrapper>' "
                    f"construction NO_SELECTIONs at NLU (the JF-551/PlayPodcast beep class)"
                )
    return warnings


def check_play_episode_season_without_episode(all_models: dict[str, dict]) -> list[str]:
    """ERROR check (JF-841 invariant, JF-814 gate-marker F4): no PlayEpisodeIntent
    sample may carry {season_number} without {episode_number} in ANY locale. The
    JF-814 handler gate elicits the season only when the EPISODE side is present
    (episodeParsed && !seasonParsed); a season-only sample family would deliver
    the symmetric shape, which still falls silently to the NextUp core, the exact
    wrong-item class JF-814 closes. Any locale gaining a season-only family must
    extend the handler gate in the SAME change; this check makes the invariant
    self-enforcing instead of memory-dependent."""
    errors: list[str] = []
    for locale, lm in sorted(all_models.items()):
        intent = intent_by_name(lm, "PlayEpisodeIntent")
        if intent is None:
            continue  # a missing intent is a cross-locale error elsewhere
        for sample in intent.get("samples", []):
            if "{season_number}" in sample and "{episode_number}" not in sample:
                errors.append(
                    f"[{locale}] PlayEpisodeIntent sample '{sample}' carries "
                    f"{{season_number}} without {{episode_number}}: a season-only ask "
                    f"still falls silently to the NextUp core (the JF-814 wrong-item "
                    f"class); extend the handler elicit gate in the same change (JF-841)"
                )
    return errors


def print_phase_warnings(warnings: list[str], all_clear: str) -> None:
    """Print one phase's findings: WARN lines, or the all-clear line when
    empty. Caller contract: extend the sink BEFORE calling; a skipped
    check never reaches here (the skip contract lives on
    run_warning_phase, which this is the report half of).
    """
    if warnings:
        for w in warnings:
            print(f"  WARN: {w}")
    else:
        print(f"  {all_clear}")


def run_warning_phase(
    title: str,
    check: Callable[[], list[str] | None],
    all_clear: str,
    sink: list[str],
) -> None:
    """Run one warning phase: print the title, run the check, extend the
    sink, then report WARN lines or the all-clear.

    A None return is a SKIPPED check: the check printed its own SKIP line
    saying why, or is documented as a silent skip (every lint here that
    returns list | None states which in its docstring), so the phase
    prints nothing more and never an all-clear over unchecked parity.
    """
    print(f"\n{title}:")
    warnings = check()
    if warnings is None:
        return  # skipped: nothing more to print (skip kinds in the docstring)
    sink.extend(warnings)
    print_phase_warnings(warnings, all_clear)


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
        run_warning_phase(
            "Cross-locale consistency",
            lambda: validate_cross_locale(all_models)
            + validate_slot_types_cross_locale(all_models),
            "All locales have consistent intents and slot types",
            all_warnings,
        )

    # Phase 3: NLU fixture carrier lint (heuristic warning check)
    if all_models:
        run_warning_phase(
            "NLU fixture carrier lint",
            lambda: lint_fixture_carriers(all_models),
            "All linted fixture utterances match a current sample carrier",
            all_warnings,
        )

    # Phase 4: BrowseCategory id-parity lint (JF-468 warning check)
    if all_models:
        run_warning_phase(
            "BrowseCategory id lint",
            lambda: lint_browse_category_ids(all_models),
            "All locales carry the shared English ids on the BrowseCategory concepts",
            all_warnings,
        )

    # Phase 5: template regen-equality check (JF-316 warning check)
    run_warning_phase(
        "Template regen equality",
        check_template_regen_equality,
        "Every templated locale regenerates its committed model byte-identically",
        all_warnings,
    )

    # Phase 6: VOICE_COMMANDS.md row-vs-model lint (JF-513.1 item 7 warning
    # check); the absent-md None is a SILENT skip (no SKIP line), unlike the
    # fixture and es-trio lints
    if all_models:
        run_warning_phase(
            "VOICE_COMMANDS row lint",
            lambda: lint_voice_commands_rows(all_models),
            "Every row maps to a model intent and lists only live utterances",
            all_warnings,
        )

    # Phase 7: PlayEpisodeIntent one-shot word-order lint (JF-549 warning check)
    if all_models:
        run_warning_phase(
            "PlayEpisode one-shot order lint",
            lambda: lint_play_episode_one_shot_order(all_models),
            "Every one-shot carrier family carries both word orders (series-first and series-last)",
            all_warnings,
        )

    # Phase 9: one-shot wrapper coverage (JF-614 warning check)
    if all_models:
        wrapper_warnings = check_wrapper_coverage(all_models)
        all_warnings.extend(wrapper_warnings)

    # Phase 10: PlayEpisode season-without-episode (JF-841 invariant, error check)
    if all_models:
        print("\nPlayEpisode season-only sample check:")
        season_only_errors = check_play_episode_season_without_episode(all_models)
        all_errors.extend(season_only_errors)
        if season_only_errors:
            for e in season_only_errors:
                print(f"  ERROR: {e}")
        else:
            print("  No sample carries {season_number} without {episode_number} in any locale")

    # Phase 11: es trio PlayEpisodeIntent mirroring lint (JF-844.1 warning check)
    if all_models:
        run_warning_phase(
            "PlayEpisode es-trio mirroring lint",
            lambda: lint_es_trio_play_episode_mirroring(all_models),
            "every listed es sibling's PlayEpisode samples are a subset of "
            "es-ES's, surplus exactly matching the whitelisted divergences",
            all_warnings,
        )

    # Phase 8: elicit-target dialog registration (JF-550 error check)
    if all_models:
        print("\nElicit dialog registration:")
        reg_errors, reg_warnings = check_elicit_dialog_registration()
        all_errors.extend(reg_errors)
        # JF-612: surface the check's warnings (the pre-existing Shape B wrapper
        # drift note plus the hoisted-array unresolved notes) - they used to be
        # discarded with `_`, so the never-silence policy had no output path.
        all_warnings.extend(reg_warnings)
        # Review round: the all-clear must never print over unverified parity.
        # Errors keep priority: a co-occurring warning prints only in the
        # summary block, never at the phase level.
        if reg_errors:
            for e in reg_errors:
                print(f"  ERROR: {e}")
        else:
            print_phase_warnings(
                reg_warnings,
                "Every handler-elicited intent is dialog-registered with slot parity in all locales",
            )

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
