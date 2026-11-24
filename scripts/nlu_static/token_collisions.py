"""Token-ownership collision lint (JF-954.2).

Finds words that are at the same time a carrier word of one intent and a
slot value (or synonym) of a custom type another intent uses: the issue
#46 class. The de-DE break behind it ("spiele das Lied die streithörnchen"
routed to MediaInfoIntent because "Lied" was a MediaInfoType synonym AND a
PlaySongIntent carrier word) is visible in the model files with no Amazon
call; this lint makes it a build-time warning.

Three finding kinds, exactly as the task defines them:

- ``CARRIER_VS_VALUE``: an entry of custom type T appears as a contiguous
  token sequence inside the carrier of at least one sample of intent I,
  where I does NOT use T and at least one other intent J uses T. One
  finding per (locale, T, entry, I).
- ``VALUE_IN_TWO_TYPES``: the same normalized entry text exists in two
  different custom types of one locale. One finding per (locale, entry,
  sorted pair of type names).
- ``CATALOG_VS_MODEL`` (snapshot only, CLI only, never baselined): a
  snapshot entry of a catalog type whose full normalized text equals a
  complete slot-free sample or an entry of a STATIC custom type.

Matching is index-driven: one inverted index per locale (token to the
(intents, sample, position) triples holding it), then each entry is matched
by looking up its FIRST token and verifying the following positions in the
carrier; no entry ever loops over all samples. Locales without word spaces
(ja-JP) are reported with the shared SKIP line and produce nothing.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from itertools import combinations
from pathlib import Path
from typing import TYPE_CHECKING

from nlu_static.effective_model import (
    DEFAULT_MODELS_DIR,
    EffectiveModel,
    build_effective_model,
    entry_texts,
)
from nlu_static.normalize import SLOT_PLACEHOLDER_RE, normalize_text, skip_line, tokenize

if TYPE_CHECKING:
    from nlu_static.effective_model import SlotTypeVocab

KIND_CARRIER_VS_VALUE = "CARRIER_VS_VALUE"
KIND_VALUE_IN_TWO_TYPES = "VALUE_IN_TWO_TYPES"
KIND_CATALOG_VS_MODEL = "CATALOG_VS_MODEL"

SUBKIND_SLOT_FREE_SAMPLE = "slot_free_sample"
SUBKIND_STATIC_ENTRY = "static_entry"

BASELINE_FILENAME = "token_collision_baseline.json"
CATALOG_EXAMPLE_CAP = 50


@dataclass(frozen=True)
class CollisionFinding:
    """One collision: its identity fields plus a human detail line.

    ``identity()`` is the sort key, the baseline key and the printed key:
    ``locale|kind|type-or-types|entry|intent-or-empty``. ``type_names``
    holds one name for CARRIER_VS_VALUE and CATALOG_VS_MODEL and the
    sorted pair for VALUE_IN_TWO_TYPES. ``intent`` is empty except for
    CARRIER_VS_VALUE, where it is the intent whose carrier contains the
    entry. ``detail`` is a deterministic human string; it never enters the
    identity.
    """

    locale: str
    kind: str
    type_names: tuple[str, ...]
    entry: str
    intent: str
    detail: str

    def identity(self) -> str:
        return "|".join(
            (self.locale, self.kind, ",".join(self.type_names), self.entry, self.intent)
        )


@dataclass(frozen=True)
class LocaleCollisions:
    """Everything the lint found for one locale, in deterministic order.

    ``skip_reason`` is the shared SKIP text when the locale cannot be
    tokenized; it scopes to the TOKEN-dependent kinds only, which then have
    empty ``findings``, while ``catalog_findings`` (full-normalized-text
    comparison, no tokenization) still runs and can carry findings.
    ``findings`` holds the baseline candidates (CARRIER_VS_VALUE +
    VALUE_IN_TWO_TYPES) sorted by identity; ``catalog_findings`` holds
    CATALOG_VS_MODEL, sorted, only when a snapshot replaced at least one
    type; ``catalog_counts`` maps sub-kind to exact count; ``notes`` are
    the effective-model's deterministic human messages (for example a
    snapshot that replaced nothing for this locale); ``catalog_truncated``
    says how many sorted catalog findings beyond the first 50 were withheld
    from ``catalog_findings`` for display purposes.
    """

    locale: str
    skip_reason: str | None
    findings: tuple[CollisionFinding, ...]
    catalog_findings: tuple[CollisionFinding, ...]
    catalog_counts: tuple[tuple[str, int], ...]
    catalog_truncated: int
    notes: tuple[str, ...] = ()


def default_models_dir() -> Path:
    """The committed models directory (test seam: monkeypatch this)."""
    return DEFAULT_MODELS_DIR


def default_baseline_path() -> Path:
    """The committed collision baseline file (test seam: monkeypatch this)."""
    return Path(__file__).resolve().parent / BASELINE_FILENAME


def committed_locales(models_dir: Path | None = None) -> list[str]:
    """The locales with a committed model_*.json, sorted."""
    directory = models_dir if models_dir is not None else default_models_dir()
    return sorted(path.stem.removeprefix("model_") for path in directory.glob("model_*.json"))


def analyze_locale(
    locale: str,
    snapshot: dict | None = None,
    models_dir: Path | None = None,
    raw_model: dict | None = None,
) -> LocaleCollisions:
    """Run every lint kind over one locale's effective model.

    Committed mode (snapshot=None) produces CARRIER_VS_VALUE and
    VALUE_IN_TWO_TYPES only; a snapshot additionally enables
    CATALOG_VS_MODEL against the types the snapshot replaced. Non
    whitespace-tokenized locales (ja-JP) get the SKIP reason on the
    TOKEN-dependent kinds only: CATALOG_VS_MODEL compares FULL normalized
    texts and needs no word tokens, so it still runs for them.
    ``raw_model`` (a languageModel dict the caller already loaded) skips
    the file read; the validator's Phase 11 threads its parsed models
    through it so the files are read once per run.
    """
    reason = skip_line(locale)
    resolved_dir = models_dir if models_dir is not None else default_models_dir()
    model = build_effective_model(
        locale, snapshot=snapshot, models_dir=resolved_dir, raw_model=raw_model
    )
    catalog, counts, truncated = _catalog_vs_model_findings(locale, model)
    if reason is not None:
        return LocaleCollisions(
            locale=locale,
            skip_reason=reason,
            findings=(),
            catalog_findings=catalog,
            catalog_counts=counts,
            catalog_truncated=truncated,
            notes=model.notes,
        )
    carrier = _carrier_vs_value_findings(locale, model)
    pairs = _value_in_two_types_findings(locale, model)
    findings = tuple(sorted(carrier + pairs, key=lambda finding: finding.identity()))
    return LocaleCollisions(
        locale=locale,
        skip_reason=None,
        findings=findings,
        catalog_findings=catalog,
        catalog_counts=counts,
        catalog_truncated=truncated,
        notes=model.notes,
    )


def _vocab_entry_texts(vocab: SlotTypeVocab) -> list[str]:
    """The entry texts of one type: each canonical and each synonym (shared helper)."""
    return entry_texts(vocab)


def _entry_tokens(text: str) -> tuple[str, ...]:
    """The normalized token tuple of one entry text, possibly empty."""
    return tuple(tokenize(text))


def _carrier_vs_value_findings(locale: str, model: EffectiveModel) -> list[CollisionFinding]:
    """CARRIER_VS_VALUE: type entries matching carriers of intents not using the type.

    Builds ONE inverted index over every carrier token of the locale (token
    to (intent, sample index, position)), then matches each entry by looking
    up the entry's first token and verifying the following positions. An
    intent that itself declares the type is never a match target; at least
    one OTHER intent must use the type for a finding to exist at all.
    """
    users: dict[str, set[str]] = {}
    for intent in model.intents:
        for slot in intent.slots:
            users.setdefault(slot.type, set()).add(intent.name)

    # One record per (intent ordinal, sample index): the carrier tokens and
    # the raw sample text live together, so the two reads cannot disagree on
    # keys. The key uses the intent's ORDINAL, not its name: a hand-authored
    # model can carry two intents with one name, and a name key would let
    # the second intent's carriers overwrite the first's while the index
    # still points at the first's positions.
    carriers: dict[tuple[int, int], tuple[tuple[str, ...], str]] = {}
    index: dict[str, list[tuple[int, int, int]]] = {}
    for ordinal, intent in enumerate(model.intents):
        for sample_index, sample in enumerate(intent.samples):
            tokens = tuple(tokenize(SLOT_PLACEHOLDER_RE.sub(" ", sample)))
            carriers[(ordinal, sample_index)] = (tokens, sample)
            for position, token in enumerate(tokens):
                index.setdefault(token, []).append((ordinal, sample_index, position))
    intent_names = [intent.name for intent in model.intents]

    findings: list[CollisionFinding] = []
    for vocab in model.types:
        using = users.get(vocab.name, set())
        if not using:
            continue
        seen: set[str] = set()
        for text in _vocab_entry_texts(vocab):
            entry_tokens = _entry_tokens(text)
            if not entry_tokens:
                continue
            key = " ".join(entry_tokens)
            if key in seen:
                continue
            seen.add(key)
            matches: dict[str, set[tuple[int, int]]] = {}
            for ordinal, sample_index, position in index.get(entry_tokens[0], ()):
                intent_name = intent_names[ordinal]
                if intent_name in using:
                    continue
                carrier = carriers[(ordinal, sample_index)][0]
                if carrier[position : position + len(entry_tokens)] == entry_tokens:
                    # A set of (ordinal, sample index): an entry occurring
                    # twice in ONE sample (or a multi-token entry matching
                    # overlapping runs) is one matching sample, not two.
                    matches.setdefault(intent_name, set()).add((ordinal, sample_index))
            for intent_name in sorted(matches):
                hits = sorted(matches[intent_name])
                example = min(carriers[hit][1] for hit in hits)
                detail = (
                    f"using intents: {', '.join(sorted(using))}; "
                    f"samples: {len(hits)}; example: {json.dumps(example, ensure_ascii=False)}"
                )
                findings.append(
                    CollisionFinding(
                        locale=locale,
                        kind=KIND_CARRIER_VS_VALUE,
                        type_names=(vocab.name,),
                        entry=key,
                        intent=intent_name,
                        detail=detail,
                    )
                )
    return findings


def _value_in_two_types_findings(locale: str, model: EffectiveModel) -> list[CollisionFinding]:
    """VALUE_IN_TWO_TYPES: one normalized entry text owned by several custom types.

    One finding per (locale, entry, sorted pair); an entry present in three
    types yields the three pairs.
    """
    entry_types: dict[str, set[str]] = {}
    for vocab in model.types:
        for text in _vocab_entry_texts(vocab):
            entry_tokens = _entry_tokens(text)
            if not entry_tokens:
                continue
            entry_types.setdefault(" ".join(entry_tokens), set()).add(vocab.name)
    findings: list[CollisionFinding] = []
    for entry in sorted(entry_types):
        names = sorted(entry_types[entry])
        for first, second in combinations(names, 2):
            findings.append(
                CollisionFinding(
                    locale=locale,
                    kind=KIND_VALUE_IN_TWO_TYPES,
                    type_names=(first, second),
                    entry=entry,
                    intent="",
                    detail=f"entry owned by both {first} and {second}",
                )
            )
    return findings


def _catalog_vs_model_findings(
    locale: str, model: EffectiveModel
) -> tuple[tuple[CollisionFinding, ...], tuple[tuple[str, int], ...], int]:
    """CATALOG_VS_MODEL: a snapshot entry that equals a slot-free sample or a STATIC entry.

    Runs only when the snapshot replaced at least one type
    (``model.source == "snapshot"``); otherwise returns empty everything.
    Comparison is the FULL normalized text on both sides (a library song
    titled "Stop" must equal the sample "stop" as a whole, not as tokens).
    When one entry matches both sub-kinds it is ONE finding whose detail
    names both, while the counts record each sub-kind separately. The
    returned findings list is capped at the first 50 in sorted order; the
    third element reports how many sorted findings were withheld.
    """
    if model.source != "snapshot":
        return (), (), 0
    slot_free_samples: set[str] = set()
    for intent in model.intents:
        for sample in intent.samples:
            if "{" in sample:
                continue
            text = normalize_text(sample)
            if text:
                slot_free_samples.add(text)
    static_texts: set[str] = set()
    for vocab in model.types:
        if vocab.kind != "STATIC":
            continue
        for text in _vocab_entry_texts(vocab):
            normalized = normalize_text(text)
            if normalized:
                static_texts.add(normalized)

    by_identity: dict[str, CollisionFinding] = {}
    counts: dict[str, int] = {SUBKIND_SLOT_FREE_SAMPLE: 0, SUBKIND_STATIC_ENTRY: 0}
    for type_name in model.snapshot_types:
        vocab = model.type_vocab(type_name)
        if vocab is None:
            continue
        # Dedupe per type by normalized text FIRST: a duplicate title (value
        # "Stop" plus a synonym "stop" on another entry) is one entry, so it
        # must count and print once, not once per raw occurrence.
        distinct: set[str] = set()
        for text in _vocab_entry_texts(vocab):
            normalized = normalize_text(text)
            if normalized:
                distinct.add(normalized)
        for normalized in sorted(distinct):
            subkinds: list[str] = []
            if normalized in slot_free_samples:
                counts[SUBKIND_SLOT_FREE_SAMPLE] += 1
                subkinds.append(SUBKIND_SLOT_FREE_SAMPLE)
            if normalized in static_texts:
                counts[SUBKIND_STATIC_ENTRY] += 1
                subkinds.append(SUBKIND_STATIC_ENTRY)
            if not subkinds:
                continue
            finding = CollisionFinding(
                locale=locale,
                kind=KIND_CATALOG_VS_MODEL,
                type_names=(type_name,),
                entry=normalized,
                intent="",
                detail="sub-kind: " + " and ".join(subkinds),
            )
            # One identity per (type, entry): a both-sub-kind entry collapses
            # into the single finding above, so identities stay unique.
            by_identity.setdefault(finding.identity(), finding)
    ordered = sorted(by_identity.values(), key=lambda finding: finding.identity())
    shown = tuple(ordered[:CATALOG_EXAMPLE_CAP])
    return shown, tuple(sorted(counts.items())), len(ordered) - len(shown)


def load_baseline(path: Path | None = None) -> list[str]:
    """Read the baseline file as a sorted list of identity strings.

    A missing file is an empty baseline (first run before --update-baseline);
    a present file must be a JSON list of strings.
    """
    baseline_path = path if path is not None else default_baseline_path()
    if not baseline_path.is_file():
        return []
    doc = json.loads(baseline_path.read_text(encoding="utf-8"))
    if not isinstance(doc, list) or not all(isinstance(item, str) for item in doc):
        raise ValueError(f"{baseline_path}: baseline must be a JSON list of identity strings")
    return sorted(doc)


def update_baseline(
    path: Path, identities: list[str], locales: list[str] | None = None
) -> tuple[int, int]:
    """Merge ``identities`` into the baseline and report the delta.

    Scoped merge: with ``locales`` given, only the entries of those locales
    are rewritten; entries of locales outside the scope (a locale this run
    did not lint, or one whose model file no longer exists) are kept
    verbatim, so a single-locale run can never delete the other locales'
    known findings. Returns (added, removed) against the previously stored
    list. The file content is byte-identical across two consecutive runs
    over the same models.
    """
    previous = load_baseline(path)
    previous_set = set(previous)
    ids = set(identities)
    scope = set(locales) if locales is not None else None
    kept = {
        identity
        for identity in previous_set
        if scope is None or identity.split("|", 1)[0] not in scope
    }
    current = sorted(kept | ids)
    path.write_text(json.dumps(current, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return len(ids - previous_set), len(previous_set - set(current))


def split_baselined(
    findings: list[CollisionFinding] | tuple[CollisionFinding, ...], baseline: list[str]
) -> tuple[list[CollisionFinding], list[CollisionFinding]]:
    """Split findings into (non-baselined, baselined) by identity.

    The ONE filtering point the validator and the CLI share, so a finding
    in the committed baseline can never produce a validator warning and a
    new collision always does.
    """
    known = set(baseline)
    non_baselined: list[CollisionFinding] = []
    baselined: list[CollisionFinding] = []
    for finding in findings:
        (baselined if finding.identity() in known else non_baselined).append(finding)
    return non_baselined, baselined


def baseline_candidates(reports: list[LocaleCollisions]) -> list[str]:
    """The identities the baseline tracks: both model-side kinds, sorted unique.

    CATALOG_VS_MODEL findings never enter the baseline (the catalog is
    user-dependent; baselining it would freeze one user's library names
    into the repo).
    """
    identities: set[str] = set()
    for report in reports:
        for finding in report.findings:
            identities.add(finding.identity())
    return sorted(identities)


def stale_identities(
    reports: list[LocaleCollisions], baseline: list[str]
) -> list[str]:
    """Baseline identities the current models no longer produce, sorted.

    Scoped to the locales of ``reports`` (the identity's first ``|`` field
    is the locale): a baseline identity of a locale this run did not lint
    is out of scope, not stale, so a single-locale run does not report the
    other locales' entries as gone.
    """
    produced = set(baseline_candidates(reports))
    scope = {report.locale for report in reports}
    stale: set[str] = set()
    for identity in baseline:
        if identity.split("|", 1)[0] in scope and identity not in produced:
            stale.add(identity)
    return sorted(stale)
