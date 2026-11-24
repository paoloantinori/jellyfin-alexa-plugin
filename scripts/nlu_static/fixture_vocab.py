"""Fixture-versus-vocabulary check (JF-954.3), the Breaking Bad class.

For every NLU fixture row and every slot it expects, this check asks one
dictionary question the live suite answers only after a two-hour run: is
the pinned value (or, for any-value rows, at least one vocabulary entry)
actually part of the effective slot type the trainer would resolve the
slot against? The concrete incident (2026-11-23): the es/fr episode rows
pinned ``series_name`` = "breaking bad" on PlayEpisodeIntent, whose slot
type SeriesName is CATALOG. The committed model's static seed contains
"Breaking Bad", so the lab matched; the live catalog replaces the seed
with the user's library series and the maintainer has no Breaking Bad. A
value that matches nothing in a catalog-backed type discourages intent
selection (project finding JF-684), so a marginal phrasing fell to another
intent. This module mechanizes anti-pattern #8 of the project CLAUDE.md
("Missing Slot Type Values for Test Fixtures").

Statuses, exactly as the task defines them:

- ``PRESENT``: the pinned value equals (after ``normalize_text``) the
  canonical value or a synonym of the effective type, or at least one
  entry occurs as a contiguous token sequence in the utterance for an
  any-value row.
- ``ABSENT``: a pinned value the effective type does not own.
- ``NO_ENTRY_IN_UTTERANCE``: an any-value row whose utterance contains no
  entry of the effective type; the row relies on the trainer generalizing
  beyond the vocabulary.
- ``FIXTURE_SLOT_UNKNOWN``: none of the expected intent alternatives
  declares the slot (a fixture authoring bug).
- ``NOT_APPLICABLE``: the slot type is not CLOSED (free-text or builtin),
  so there is no vocabulary to check against.
- ``ABSENT_IN_LIBRARY_SEED_ONLY``: snapshot mode only. A pinned value
  missing from a NAME-LEVEL snapshot (producer ``jellyfin-names``) but
  present in the committed seed of the replaced CATALOG type. Such a value
  is probably present live (the plugin merges committed seeds into the
  real payload for several types), so it must never be reported as plain
  ``ABSENT``; under the authoritative producer of the JF-954.8 plugin
  export the payload already holds whatever the plugin merges, so the
  same case is plain ``ABSENT``.

Locales without word spaces (ja-JP) run the any-value rule as substring
containment on ``normalize_text`` output instead of token sequences; there
are no SKIP lines here because both rules work without word tokens.

The checker itself is the pure function ``check_fixture_rows``; the
validator's Phase 12 and the ``fixtures`` CLI subcommand are thin wrappers.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import yaml

from nlu_static import token_collisions
from nlu_static.effective_model import (
    DEFAULT_MODELS_DIR,
    EffectiveModel,
    REPO_ROOT,
    build_effective_model,
    classify_slot_type,
    entry_texts,
    is_catalog_type,
)
from nlu_static.normalize import is_whitespace_tokenized, normalize_text, tokenize

STATUS_PRESENT = "PRESENT"
STATUS_ABSENT = "ABSENT"
STATUS_NO_ENTRY_IN_UTTERANCE = "NO_ENTRY_IN_UTTERANCE"
STATUS_FIXTURE_SLOT_UNKNOWN = "FIXTURE_SLOT_UNKNOWN"
STATUS_NOT_APPLICABLE = "NOT_APPLICABLE"
STATUS_ABSENT_IN_LIBRARY_SEED_ONLY = "ABSENT_IN_LIBRARY_SEED_ONLY"

# Producers whose snapshot lists raw library NAMES only (no seed merge, no
# phonetic synonyms). producer_jellyfin.PRODUCER_NAME is duplicated here as a
# literal so the heavy producer module (urllib) stays out of this import path;
# a test pins the two spellings together. The authoritative JF-954.8 producer
# is deliberately absent from this set: its payload already contains whatever
# the plugin merges, so its misses are trustworthy plain ABSENT.
NAME_LEVEL_PRODUCERS: frozenset[str] = frozenset({"jellyfin-names"})

DEFAULT_FIXTURES_DIR = REPO_ROOT / "tests" / "integration" / "fixtures"


@dataclass(frozen=True)
class FixtureVocabResult:
    """One slot expectation of one fixture row, with its verdict.

    Field names are the JF-954.3 JSON contract (the triage subtask imports
    them); do not rename, and keep ``payload`` in sync. ``type`` is the
    resolved slot type name and empty for FIXTURE_SLOT_UNKNOWN;
    ``type_kind`` is STATIC or CATALOG for a custom type and empty for a
    builtin family or an unresolved slot; ``mode`` is the effective model's
    source (``committed`` or ``snapshot``).
    """

    locale: str
    utterance: str
    expected_intent: str
    slot: str
    type: str
    type_kind: str
    status: str
    mode: str

    def sort_key(self) -> tuple[str, str, str, str, str]:
        """The deterministic total order for result lists and JSON output."""
        return (
            self.locale,
            self.utterance,
            self.expected_intent,
            self.slot,
            self.status,
        )

    def payload(self) -> dict:
        """The exact eight-key JSON object this result serializes to.

        The ONE serialization of the JSON contract: the CLI, the triage
        subtask and any future consumer emit the result through this
        method, so the field names cannot drift between producers.
        """
        return {
            "locale": self.locale,
            "utterance": self.utterance,
            "expected_intent": self.expected_intent,
            "slot": self.slot,
            "type": self.type,
            "type_kind": self.type_kind,
            "status": self.status,
            "mode": self.mode,
        }


def default_models_dir() -> Path:
    """The committed models directory (test seam: monkeypatch this)."""
    return DEFAULT_MODELS_DIR


def default_fixtures_dir() -> Path:
    """The committed NLU fixtures directory (test seam: monkeypatch this)."""
    return DEFAULT_FIXTURES_DIR


def committed_locales() -> list[str]:
    """The locales with a committed model_*.json, sorted (shared with the token lint)."""
    return token_collisions.committed_locales()


def load_fixture_rows(fixtures_dir: str | Path, locale: str) -> tuple[list | None, str | None]:
    """Load one locale's NLU fixture ``tests`` list.

    Returns (rows, None) on success and (None, skip_reason) when the locale
    cannot be checked, with a reason that names the locale (parent rule:
    silent skips are forbidden). ``e2e_*.yaml`` files are a different
    format and are never read here because the lookup is ``<locale>.yaml``
    exactly.
    """
    directory = Path(fixtures_dir)
    path = directory / f"{locale}.yaml"
    if not path.is_file():
        return None, f"SKIP {locale}: no fixture file"
    try:
        data = yaml.safe_load(path.read_text(encoding="utf-8"))
    except yaml.YAMLError as err:
        return None, f"SKIP {locale}: fixture is not parseable YAML ({err})"
    if not isinstance(data, dict):
        return None, f"SKIP {locale}: fixture is not a YAML mapping"
    rows = data.get("tests")
    if not isinstance(rows, list):
        return None, f"SKIP {locale}: fixture 'tests' is not a list"
    return rows, None


@dataclass(frozen=True)
class _CheckContext:
    """The per-run state one check_fixture_rows call shares across rows.

    ``intent_slot_types`` maps intent name to slot name to declared type,
    built once per model; ``vocab_norms`` memoizes, per type name, the
    normalized entry texts (pin comparisons) and entry token tuples
    (any-value matches), so a library-sized snapshot vocabulary is
    normalized and tokenized once per locale run, not once per row.
    """

    model: EffectiveModel
    committed_model: EffectiveModel | None
    producer: object
    substring_mode: bool
    intent_slot_types: dict[str, dict[str, str]]
    vocab_norms: dict


def check_fixture_rows(
    effective_model: EffectiveModel,
    fixture_rows: list,
    committed_model: EffectiveModel | None = None,
    snapshot: dict | None = None,
) -> list[FixtureVocabResult]:
    """Check every fixture row's slot expectations against the effective model.

    Pure function: no I/O, no globals. ``committed_model`` (the same locale
    built WITHOUT the snapshot) and ``snapshot`` only matter in snapshot
    mode, where they decide between plain ``ABSENT`` and
    ``ABSENT_IN_LIBRARY_SEED_ONLY`` per the producer rule; committed mode
    needs neither. Results are sorted by ``sort_key`` so the output is
    byte-identical across runs regardless of fixture dict ordering.
    """
    ctx = _CheckContext(
        model=effective_model,
        committed_model=committed_model,
        producer=snapshot.get("producer") if isinstance(snapshot, dict) else None,
        substring_mode=not is_whitespace_tokenized(effective_model.locale),
        intent_slot_types=_intent_slot_types(effective_model),
        vocab_norms={},
    )

    results: list[FixtureVocabResult] = []
    for row in fixture_rows:
        if not isinstance(row, dict):
            continue
        utterance = row.get("utterance")
        expected_intent = row.get("expected_intent")
        slots = row.get("expected_slots")
        if not isinstance(utterance, str) or not utterance.strip():
            continue
        if not isinstance(expected_intent, str) or not expected_intent.strip():
            continue
        if not isinstance(slots, dict):
            continue
        utterance_tokens = tuple(tokenize(utterance))
        utterance_text = normalize_text(utterance)
        alternatives = [name.strip() for name in expected_intent.split("|") if name.strip()]
        for slot_name in sorted(slots, key=str):
            # Explicit slot sort: the verdict list never depends on the YAML
            # mapping's insertion order.
            expectation = slots[slot_name]
            results.append(
                _check_one_slot(
                    ctx,
                    utterance,
                    utterance_tokens,
                    utterance_text,
                    expected_intent,
                    alternatives,
                    str(slot_name),
                    expectation,
                )
            )
    return sorted(results, key=lambda result: result.sort_key())


def _check_one_slot(
    ctx: _CheckContext,
    utterance: str,
    utterance_tokens: tuple[str, ...],
    utterance_text: str,
    expected_intent: str,
    alternatives: list[str],
    slot_name: str,
    expectation: object,
) -> FixtureVocabResult:
    """Resolve one (row, slot) expectation and return its verdict."""
    model = ctx.model

    def result(slot_type: str, type_kind: str, status: str) -> FixtureVocabResult:
        return FixtureVocabResult(
            locale=model.locale,
            utterance=utterance,
            expected_intent=expected_intent,
            slot=slot_name,
            type=slot_type,
            type_kind=type_kind,
            status=status,
            mode=model.source,
        )

    declared = [
        ctx.intent_slot_types.get(name, {}).get(slot_name, "")
        for name in alternatives
    ]
    declared = [type_name for type_name in declared if type_name]
    if not declared:
        return result("", "", STATUS_FIXTURE_SLOT_UNKNOWN)
    # The repo's error-level slot-consistency check already guarantees one
    # slot name maps to one type per locale; the alphabetical pick only pins
    # that invariant deterministically for synthetic models that break it.
    type_name = sorted(declared)[0]
    if classify_slot_type(type_name, model) != "CLOSED":
        return result(type_name, "", STATUS_NOT_APPLICABLE)
    vocab = model.type_vocab(type_name)
    type_kind = vocab.kind if vocab is not None else _kind_of_type_name(type_name)
    pin_norms, entry_token_runs = _vocab_norms(ctx, type_name, vocab)

    pin = _pinned_value(expectation)
    if pin is not None:
        if pin in pin_norms:
            return result(type_name, type_kind, STATUS_PRESENT)
        if (
            model.source == "snapshot"
            and type_kind == "CATALOG"
            and vocab is not None
            and type_name in model.snapshot_types
            and isinstance(ctx.producer, str)
            and ctx.producer in NAME_LEVEL_PRODUCERS
            and _committed_has(ctx.committed_model, type_name, pin)
        ):
            return result(type_name, type_kind, STATUS_ABSENT_IN_LIBRARY_SEED_ONLY)
        return result(type_name, type_kind, STATUS_ABSENT)

    # Any-value expectation: the row passes only if the trainer can anchor
    # the slot on vocabulary it actually holds.
    if ctx.substring_mode:
        # Script-aware containment (review round): the substring rule
        # exists for CJK text without word spaces, where a sub-word match
        # ('Yoru' inside 'Kon-ya') is the intended semantics, so a CJK
        # entry keeps plain substring. A LATIN entry must not match inside
        # an unrelated Latin run ('pop' inside 'popcorn'): it counts only
        # when both edges are the string's ends or non-alphanumeric
        # characters (so a bare Latin title jammed flush against Japanese
        # text reads NO_ENTRY, the conservative verdict).
        if any(
            text and (
                _substring_bounded(utterance_text, text)
                if _is_latin_text(text)
                else text in utterance_text
            )
            for text in pin_norms
        ):
            return result(type_name, type_kind, STATUS_PRESENT)
    elif any(_contains_contiguous(utterance_tokens, run) for run in entry_token_runs):
        return result(type_name, type_kind, STATUS_PRESENT)
    return result(type_name, type_kind, STATUS_NO_ENTRY_IN_UTTERANCE)


def _intent_slot_types(model: EffectiveModel) -> dict[str, dict[str, str]]:
    """Intent name -> slot name -> declared type, built once per model.

    An intent that declares the same slot name twice keeps its last entry;
    the repo's slot-consistency check makes that unreachable in real models.
    """
    index: dict[str, dict[str, str]] = {}
    for intent in model.intents:
        index[intent.name] = {slot.name: slot.type for slot in intent.slots if slot.type}
    return index


def _vocab_norms(
    ctx: _CheckContext, type_name: str, vocab
) -> tuple[frozenset[str], tuple[tuple[str, ...], ...]]:
    """One locale-run's normalization of one type's vocabulary, memoized.

    The first element is every normalized entry text (canonicals and
    synonyms, pin comparisons and substring matching share it); the second
    is every entry's token tuple, empty runs dropped (contiguous matching).
    Empty entries never match anything, so they are absent from both.
    """
    cached = ctx.vocab_norms.get(type_name)
    if cached is None:
        if vocab is None:
            # A CLOSED type name the model does not declare: empty vocabulary,
            # so every pin misses and every any-value row finds nothing.
            cached = (frozenset(), ())
        else:
            texts = entry_texts(vocab)
            pin_norms = frozenset(normalize_text(text) for text in texts) - {""}
            runs = tuple(run for run in (tuple(tokenize(text)) for text in texts) if run)
            cached = (pin_norms, runs)
        ctx.vocab_norms[type_name] = cached
    return cached


def _kind_of_type_name(type_name: str) -> str:
    """The kind a CLOSED type would have, for names the model does not declare."""
    return "CATALOG" if is_catalog_type(type_name) else "STATIC"


def _pinned_value(expectation: object) -> str | None:
    """The pinned value of a slot expectation, or None for any-value rows.

    ``{}`` and ``None`` (a bare ``slot:`` in YAML) mean any non-empty value.
    A bare YAML string pins that string (the committed fixtures use the
    shorthand, for example ``titleKeywords: lange nacht``), as does a
    mapping with a string ``value``. Every other shape (a mapping with a
    non-string ``value``, a numeric scalar) cannot be read as a pin and
    degrades to the any-value check, so the slot still gets a verdict
    instead of being skipped silently.
    """
    if isinstance(expectation, str):
        return normalize_text(expectation)
    if isinstance(expectation, dict):
        value = expectation.get("value")
        if isinstance(value, str):
            return normalize_text(value)
    return None


def _contains_contiguous(utterance_tokens: tuple[str, ...], entry_tokens: tuple[str, ...]) -> bool:
    """True when ``entry_tokens`` occur back-to-back inside ``utterance_tokens``."""
    span = len(entry_tokens)
    return any(
        utterance_tokens[i : i + span] == entry_tokens
        for i in range(len(utterance_tokens) - span + 1)
    )


def _is_latin_text(text: str) -> bool:
    """True when ``text`` carries at least one ASCII letter and no CJK char.

    The boundary rule applies to Latin entries only; CJK entries keep the
    plain substring semantics (sub-word matches are intended there).
    """
    has_ascii = False
    for ch in text:
        if ch.isascii() and ch.isalpha():
            has_ascii = True
        elif not ch.isascii() and not ch.isalnum():
            return False
        elif ch.isspace():
            continue
    return has_ascii


def _substring_bounded(haystack: str, needle: str) -> bool:
    """True when ``needle`` occurs in ``haystack`` delimited on both sides.

    Both edges must be the string's ends or non-alphanumeric characters, so
    'pop' matches 'pop musik' and '(pop)' but never 'popcorn'.
    """
    if not needle:
        return False
    start = 0
    while True:
        found = haystack.find(needle, start)
        if found < 0:
            return False
        end = found + len(needle)
        left_ok = found == 0 or not haystack[found - 1].isalnum()
        right_ok = end == len(haystack) or not haystack[end].isalnum()
        if left_ok and right_ok:
            return True
        start = found + 1


def _committed_has(committed_model: EffectiveModel | None, type_name: str, pin: str) -> bool:
    """True when the committed seed of ``type_name`` owns the pinned value.

    Shares the normalized-entry-text computation with the pin check via
    ``entry_texts`` (one extraction, review round); the committed model is
    a different model from the effective one, so its norms are computed
    here per call (the call count is one per ABSENT pin under a name-level
    snapshot, bounded by the fixture row count).
    """
    if committed_model is None:
        return False
    vocab = committed_model.type_vocab(type_name)
    if vocab is None:
        return False
    return pin in {normalize_text(text) for text in entry_texts(vocab)}


def partition_validator_findings(
    results: list[FixtureVocabResult],
) -> tuple[list[FixtureVocabResult], int]:
    """The JF-954.3 validator partition (AC 11): (individual warnings, catalog count).

    Committed-mode rows that depend on the live catalog (ABSENT or
    NO_ENTRY_IN_UTTERANCE on a CATALOG type) are EXPECTED in committed mode:
    the fixtures deliberately pin library-only values such as
    "adolescencia" that the committed seed lacks, so they get ONE summary
    count instead of one warning each. Everything actionable stays an
    individual warning: ABSENT on a STATIC type, NO_ENTRY_IN_UTTERANCE on a
    STATIC type, FIXTURE_SLOT_UNKNOWN (whose verdict does not depend on any
    vocabulary), and every miss on a SNAPSHOT-mode result (under the
    authoritative producer a miss is a trustworthy actionable finding, so a
    snapshot consumer passing snapshot results here must not see them
    counted away). PRESENT, NOT_APPLICABLE and ABSENT_IN_LIBRARY_SEED_ONLY
    (a snapshot-only status) are in neither bucket.
    """
    individual: list[FixtureVocabResult] = []
    catalog_dependent = 0
    for result in results:
        if result.status == STATUS_FIXTURE_SLOT_UNKNOWN:
            individual.append(result)
        elif result.status in (STATUS_ABSENT, STATUS_NO_ENTRY_IN_UTTERANCE):
            if result.type_kind == "CATALOG" and result.mode == "committed":
                catalog_dependent += 1
            else:
                individual.append(result)
    return individual, catalog_dependent


def unknown_builtin_type_names(
    model: EffectiveModel, results: list[FixtureVocabResult]
) -> list[str]:
    """The slot type names the pinned classify table does not know, sorted.

    classify_slot_type's contract requires every caller to REPORT the names
    its table misses (so the table gets maintained instead of silently
    guessing); this is the one reporter for this module's callers. Only
    NOT_APPLICABLE results carry a type the check declined to inspect, so
    only those are re-classified here.
    """
    names: set[str] = set()
    for result in results:
        if result.status == STATUS_NOT_APPLICABLE and result.type:
            if classify_slot_type(result.type, model) == "UNKNOWN_BUILTIN":
                names.add(result.type)
    return sorted(names)


def is_reportable(result: FixtureVocabResult) -> bool:
    """Whether a result is a FINDING at all, as opposed to a clean verdict.

    The ONE "is this a finding" decision, shared by the CLI's text output
    and any later consumer, so the reportable set cannot drift between
    wrappers. (What the VALIDATOR warns about individually is the separate,
    committed-mode partition above.)
    """
    return result.status not in (STATUS_PRESENT, STATUS_NOT_APPLICABLE)


def format_warning(result: FixtureVocabResult) -> str:
    """The one-line warning text for an individual committed-mode finding."""
    if result.status == STATUS_FIXTURE_SLOT_UNKNOWN:
        return (
            f"FIXTURE_SLOT_UNKNOWN: no expected intent declares slot '{result.slot}' "
            f"(expected {result.expected_intent}; utterance \"{result.utterance}\")"
        )
    if result.status == STATUS_ABSENT:
        return (
            f"ABSENT: the pinned value for slot '{result.slot}' is not a value or synonym "
            f"of static type {result.type} (utterance \"{result.utterance}\", "
            f"expected {result.expected_intent})"
        )
    return (
        f"NO_ENTRY_IN_UTTERANCE: no entry of static type {result.type} occurs in "
        f"\"{result.utterance}\" (slot '{result.slot}', expected {result.expected_intent})"
    )


def analyze_locale(
    locale: str,
    snapshot: dict | None = None,
    models_dir: Path | None = None,
    fixtures_dir: Path | None = None,
    raw_model: dict | None = None,
) -> tuple[list[FixtureVocabResult], str | None, list[str]]:
    """One locale end to end: fixture rows plus effective model to results.

    Shared by the CLI and the validator so both wrappers stay thin. Returns
    (results, None, unknown_builtin_type_names) on success and ([], skip,
    []) when the locale cannot be checked (the fixture side is the only
    skip source; the model side raises, as build_effective_model does). The
    third element names the slot types the pinned classify table does not
    know (the classify contract requires callers to report them); in
    snapshot mode the committed model is built too, so
    ABSENT_IN_LIBRARY_SEED_ONLY can be decided. ``raw_model`` threads an
    already-parsed languageModel dict through (the validator's parsed
    models), skipping the file read exactly like the token lint does.
    """
    rows, skip = load_fixture_rows(
        fixtures_dir if fixtures_dir is not None else default_fixtures_dir(), locale
    )
    if rows is None:
        return [], skip, []
    resolved_dir = models_dir if models_dir is not None else default_models_dir()
    effective = build_effective_model(
        locale, snapshot=snapshot, models_dir=resolved_dir, raw_model=raw_model
    )
    committed = None
    if snapshot is not None:
        # raw_model rides the committed comparison too: the ABSENT vs
        # ABSENT_IN_LIBRARY_SEED_ONLY decision must compare the pin against
        # the SAME model the effective build started from (review round:
        # ignoring raw_model here read the models-dir file and produced
        # plain ABSENT where the contract demands the seed-only verdict).
        committed = build_effective_model(
            locale, models_dir=resolved_dir, raw_model=raw_model)
    results = check_fixture_rows(effective, rows, committed_model=committed, snapshot=snapshot)
    return results, None, unknown_builtin_type_names(effective, results)
