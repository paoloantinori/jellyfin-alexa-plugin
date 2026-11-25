"""Effective-model identity and change-driven fixture selection (JF-954.7).

Three pieces, one module:

Part A hashes a locale model CANONICALLY (keys sorted, compact separators,
UTF-8, no ASCII escaping; sample order kept verbatim), so two models that
differ only in key order or whitespace share one sha256, while any changed
sample, slot, type value or synonym changes it. Part B compares a base git
ref against the working tree, computes the CHANGED INTENTS (added, removed,
samples or slots differ, a declared slot type changed, or the invocation
name changed), adds the NEIGHBOURS from the subsumption graphs of BOTH the
old and the new model, and intersects the result with the locale's NLU
fixture rows. Part C is the pure side of the suite hook: the filter-file
loader and the ``should_keep`` decision the conftest hook calls.

The safety rule is absolute: selection must never silently drop coverage.
Anything that prevents a precise answer for a locale (an unreadable base
model, an unreadable working-tree model, a graph build error, a hash change
with no attributable intent change) falls that locale back to ALL of its
rows with an explicit reason in the payload. A locale whose model file does
not exist at the base is not a fallback: it is a locale added after the
base, so every intent counts as changed.

These tools detect necessary conditions and shrink the live suite. They do
not predict how Amazon's trainer routes an utterance; the full suite
remains the release gate (see docs/nlu-static-analysis.md).
"""

from __future__ import annotations

import copy
import hashlib
import json
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import NamedTuple

from nlu_static import fixture_vocab
from nlu_static import graph as nlu_graph
from nlu_static.effective_model import (
    EffectiveModel,
    _parse_snapshot_entries,
    resolve_locale_table,
    build_effective_model,
    is_catalog_type,
    load_locale_model,
    unwrap_language_model,
    validate_snapshot,
)
from nlu_static.normalize import is_whitespace_tokenized, normalize_text

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_MODEL_REL_PATH = "Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_{locale}.json"
IDENTITY_SCHEMA = 1
SELECTION_SCHEMA = 1


# --------------------------------------------------------------- Part A


def canonical_bytes(model: dict) -> bytes:
    """The canonical JSON encoding of one languageModel dict.

    Keys sorted, compact separators, UTF-8 without ASCII escaping. List
    order (samples, slot types' value lists) is content and is kept
    verbatim: a sample reorder must change the hash.
    """
    return json.dumps(model, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def model_sha256(model: dict) -> str:
    """sha256 over ``canonical_bytes``; equal models hash equal."""
    return hashlib.sha256(canonical_bytes(model)).hexdigest()


def committed_sha256(locale: str, models_dir: str | Path | None = None) -> str:
    """The canonical hash of the committed ``model_<locale>.json``."""
    return model_sha256(load_locale_model(locale, models_dir))


def snapshot_replaced_model(raw: dict, snapshot: dict, locale: str) -> dict:
    """The raw languageModel with catalog-backed types replaced by snapshot entries.

    The same replacement rules ``build_effective_model`` applies (per-locale
    entry overrides the ``*`` entry; only catalog-backed types are
    replaceable), but on the RAW dict so unknown languageModel keys survive
    into the hash. Snapshot-replaced entries are sorted so the effective
    hash is independent of the snapshot's entry order; committed entries
    keep their file order. The input dict is not mutated. The two private
    effective_model helpers are imported on purpose: table resolution and
    entry parsing must stay ONE definition each.
    """
    validate_snapshot(snapshot)
    table = resolve_locale_table(snapshot, locale)
    effective = copy.deepcopy(raw)
    if table is None:
        return effective
    for type_entry in effective.get("types", []):
        if not isinstance(type_entry, dict):
            continue
        name = type_entry.get("name")
        if isinstance(name, str) and is_catalog_type(name) and name in table:
            entries = _parse_snapshot_entries(table[name], f"snapshot locales.{locale}.{name}")
            type_entry["values"] = [
                {"name": {"value": value, "synonyms": list(synonyms)}}
                for value, synonyms in sorted(entries)
            ]
    return effective


def effective_sha256(locale: str, snapshot: dict, models_dir: str | Path | None = None) -> str:
    """The canonical hash of the snapshot-replaced committed model."""
    raw = load_locale_model(locale, models_dir)
    return model_sha256(snapshot_replaced_model(raw, snapshot, locale))


def identity_entries(
    locales: list[str], snapshot: dict | None, models_dir: str | Path | None = None
) -> dict[str, dict]:
    """The per-locale identity payloads the identity subcommand prints.

    Without a snapshot every entry carries ``committed_sha256``; with one it
    also carries ``effective_sha256``. A locale whose committed model cannot
    be read raises (the CLI refuses explicitly; a typo'd locale must never
    read as all-clean).
    """
    entries: dict[str, dict] = {}
    for locale in locales:
        raw = load_locale_model(locale, models_dir)
        entry = {"committed_sha256": model_sha256(raw)}
        if snapshot is not None:
            entry["effective_sha256"] = model_sha256(snapshot_replaced_model(raw, snapshot, locale))
        entries[locale] = entry
    return entries


def render_identity_text(entries: dict[str, dict], snapshot_arg: str | None) -> str:
    """The deterministic one-line-per-locale identity text form."""
    suffix = f" (snapshot: {snapshot_arg})" if snapshot_arg else ""
    lines = [f"identity over {len(entries)} locale(s){suffix}"]
    for locale in sorted(entries):
        line = f"  {locale} committed_sha256={entries[locale]['committed_sha256']}"
        if "effective_sha256" in entries[locale]:
            line += f" effective_sha256={entries[locale]['effective_sha256']}"
        lines.append(line)
    return "\n".join(lines) + "\n"


# --------------------------------------------------------------- Part B


class ModelMissingAtBase(Exception):
    """The model file does not exist at the base ref (a locale added after it)."""


class GitShowError(Exception):
    """``git show`` failed for a reason other than a missing path."""


def _first_line(text: str | None) -> str:
    for line in (text or "").splitlines():
        if line.strip():
            return line.strip()
    return ""


def verify_git_ref(base: str, repo_root: Path | None = None) -> None:
    """Refuse an unresolvable base ref before any per-locale work starts."""
    root = Path(repo_root) if repo_root is not None else REPO_ROOT
    result = subprocess.run(
        ["git", "rev-parse", "--verify", base],
        capture_output=True,
        text=True,
        check=False,
        cwd=root,
    )
    if result.returncode != 0:
        detail = _first_line(result.stderr) or f"git rev-parse exited {result.returncode}"
        raise ValueError(f"git cannot resolve ref {base!r}: {detail}")


def git_show_model(base: str, locale: str, repo_root: Path | None = None) -> dict:
    """The languageModel dict at ``git show <base>:<model path>``.

    Raises ModelMissingAtBase when the path does not exist at the ref,
    GitShowError when git fails for any other reason, and ValueError for
    model JSON that does not parse or unwrap. The ONE git-show plumbing:
    the CLI's graph diff-git note wrapper delegates here.
    """
    path = DEFAULT_MODEL_REL_PATH.format(locale=locale)
    root = Path(repo_root) if repo_root is not None else REPO_ROOT
    # Existence is decided by exit code (git cat-file -e), never by matching
    # git's stderr message: under a non-English git locale the message is
    # translated and message matching would misclassify a newly added locale
    # as unreadable instead of fully changed. cat-file -e also fails for an
    # unresolvable ref, which the select CLI rules out up front and the
    # diff-git note wrapper maps to its own "not in <base>" note either way.
    exists = subprocess.run(
        ["git", "cat-file", "-e", f"{base}:{path}"],
        capture_output=True,
        check=False,
        cwd=root,
    )
    if exists.returncode != 0:
        raise ModelMissingAtBase(f"{path} does not exist at {base}")
    result = subprocess.run(
        ["git", "show", f"{base}:{path}"],
        capture_output=True,
        text=True,
        check=False,
        cwd=root,
    )
    if result.returncode != 0:
        detail = _first_line(result.stderr) or f"git show exited {result.returncode}"
        raise GitShowError(detail)
    doc = json.loads(result.stdout)
    if not isinstance(doc, dict):
        raise ValueError("top level must be a JSON object")
    return unwrap_language_model(doc)


def changed_sets(old: EffectiveModel, new: EffectiveModel) -> tuple[set[str], set[str]]:
    """(changed intents, changed slot types) between two effective models.

    An intent is changed when it was added, removed, its samples differ
    (order included), its slots differ, or it declares a slot whose TYPE is
    changed (values or synonyms differ, type added or removed), checked
    against both models so a removed intent declaring the type is caught
    too. Entry and sample comparisons are element-wise, so a reorder is a
    change (the canonical hash changes with it).
    """
    old_intents = {intent.name: intent for intent in old.intents}
    new_intents = {intent.name: intent for intent in new.intents}
    changed: set[str] = (new_intents.keys() - old_intents.keys()) | (
        old_intents.keys() - new_intents.keys()
    )
    for name in old_intents.keys() & new_intents.keys():
        before, after = old_intents[name], new_intents[name]
        if before.samples != after.samples or before.slots != after.slots:
            changed.add(name)
    old_types = {vocab.name: vocab.entries for vocab in old.types}
    new_types = {vocab.name: vocab.entries for vocab in new.types}
    changed_types: set[str] = (new_types.keys() - old_types.keys()) | (
        old_types.keys() - new_types.keys()
    )
    for name in old_types.keys() & new_types.keys():
        if old_types[name] != new_types[name]:
            changed_types.add(name)
    for model in (old, new):
        for intent in model.intents:
            if any(spec.type in changed_types for spec in intent.slots):
                changed.add(intent.name)
    return changed, changed_types


def neighbour_set(changed: set[str], graphs: list[dict]) -> set[str]:
    """Every intent edge-connected (either direction) to a changed intent.

    Edges from every graph handed in (the old model's and the new model's);
    the changed intents themselves are not their own neighbours.
    """
    neighbours: set[str] = set()
    for graph in graphs:
        for edge in graph.get("edges", []):
            source, target = edge["from"], edge["to"]
            if source in changed:
                neighbours.add(target)
            if target in changed:
                neighbours.add(source)
    return neighbours - changed


class LocaleSelection(NamedTuple):
    """One locale's select result plus the counts the totals need.

    ``rows_selected`` is the length of the payload's deduplicated ``rows``
    (distinct selected utterances); ``rows_total`` is the locale's fixture
    entry count; ``malformed`` counts entries without a string utterance or
    expected_intent.
    """

    payload: dict
    rows_total: int
    rows_selected: int
    malformed: int


def _fallback_from_rows(fixture_rows: list, reason: str, rows_total: int) -> LocaleSelection:
    """The safety-rule payload: every utterance-bearing row, with the reason.

    ``rows`` is deduplicated so ``rows_selected`` always equals
    ``len(payload["rows"])`` (review round, finding 5): the totals count
    distinct selected utterances while ``rows_total`` counts fixture
    entries.
    """
    rows = sorted({
        row["utterance"]
        for row in fixture_rows
        if isinstance(row, dict) and isinstance(row.get("utterance"), str)
    })
    payload = {
        "status": "fallback_all",
        "reason": reason,
        "changed_intents": [],
        "neighbour_intents": [],
        "rows": rows,
    }
    return LocaleSelection(payload, rows_total, len(rows), 0)


def _select_rows(fixture_rows: list, selected: set[str]) -> tuple[list[str], int]:
    """(deduplicated selected utterances, sorted; malformed row count).

    A row is selected when any of its ``A|B`` alternatives is in the
    selected intent set. A row without a string utterance or expected_intent
    is counted as malformed so the skip stays explicit instead of silent.
    Deduplicating here keeps the selected count (the list's length) equal
    to the payload's ``rows`` even when a fixture repeats one utterance
    (review round, finding 5).
    """
    rows: set[str] = set()
    malformed = 0
    for row in fixture_rows:
        if not isinstance(row, dict):
            malformed += 1
            continue
        utterance = row.get("utterance")
        expected = row.get("expected_intent")
        if not isinstance(utterance, str) or not isinstance(expected, str):
            malformed += 1
            continue
        if any(alt.strip() in selected for alt in expected.split("|")):
            rows.add(utterance)
    return sorted(rows), malformed


def select_locale(
    locale: str,
    old_raw: dict | None,
    new_raw: dict,
    fixture_rows: list,
    *,
    neighbours: bool = True,
) -> LocaleSelection:
    """The per-locale select computation over parsed model dicts.

    ``old_raw`` is the base ref's languageModel dict, or None when the file
    does not exist at the base (every intent counts as changed). Any failure
    to build a model or a graph falls the locale back to all of its rows;
    the broad except clauses are the mandated fallback contract, not sloppiness.
    """
    rows_total = len(fixture_rows)
    if old_raw is not None and model_sha256(old_raw) == model_sha256(new_raw):
        return LocaleSelection({"status": "unchanged"}, rows_total, 0, 0)
    try:
        new_model = build_effective_model(locale, raw_model=new_raw)
    except Exception as err:  # noqa: BLE001; the fallback contract covers any shape
        return _fallback_from_rows(fixture_rows, f"working-tree model unreadable: {err}", rows_total)
    old_model: EffectiveModel | None = None
    note: str | None = None
    if old_raw is None:
        note = "model file not present at the base; every intent counts as changed"
    else:
        try:
            old_model = build_effective_model(locale, raw_model=old_raw)
        except Exception as err:  # noqa: BLE001; the fallback contract covers any shape
            return _fallback_from_rows(fixture_rows, f"model at base unreadable: {err}", rows_total)
    new_names = {intent.name for intent in new_model.intents}
    old_names = {intent.name for intent in old_model.intents} if old_model is not None else set()
    if old_model is None:
        changed: set[str] = set(new_names)
    else:
        changed, _changed_types = changed_sets(old_model, new_model)
        if old_raw.get("invocationName") != new_raw.get("invocationName"):
            changed = old_names | new_names
    if not changed:
        # Hashes differ but nothing was attributable (a type no intent uses,
        # a languageModel key outside intents/types): selecting nothing here
        # would silently drop coverage, so the safety rule applies.
        return _fallback_from_rows(
            fixture_rows,
            "model hashes differ but no intent-level change was identified",
            rows_total,
        )
    if neighbours and not is_whitespace_tokenized(locale):
        # ja-JP has no sample graph (no word spaces, the shared skip
        # convention): a changed ja-JP locale selects ALL of its intents and
        # the payload says so in the note.
        selected = old_names | new_names
        extra = (
            f"no sample graph for {locale} (no whitespace tokenization); "
            "every intent is selected"
        )
        note = f"{note}; {extra}" if note else extra
    elif neighbours:
        graphs: list[dict] = []
        for model in ([old_model] if old_model is not None else []) + [new_model]:
            try:
                graphs.append(nlu_graph.build_graph(locale, model))
            except Exception as err:  # noqa: BLE001; the fallback contract covers any shape
                return _fallback_from_rows(fixture_rows, f"graph build failed: {err}", rows_total)
        selected = changed | neighbour_set(changed, graphs)
    else:
        selected = set(changed)
    rows, malformed = _select_rows(fixture_rows, selected)
    payload = {
        "status": "changed",
        "changed_intents": sorted(changed),
        "neighbour_intents": sorted(selected - changed),
        "rows": rows,
    }
    if note is not None:
        payload["note"] = note
    return LocaleSelection(payload, rows_total, len(rows), malformed)


def compute_selection(
    base: str,
    locales: list[str],
    *,
    neighbours: bool = True,
    fixtures_dir: str | Path | None = None,
    models_dir: str | Path | None = None,
    strict_locale: bool = False,
    repo_root: Path | None = None,
) -> tuple[dict, list[str]]:
    """The full select payload over the given locales plus stderr note lines.

    The caller passes the locale list (sorted). Fixture rows come from
    ``<fixtures_dir>/<locale>.yaml`` (never ``e2e_*.yaml``); the old model
    comes from ``git show`` and the new one from the working tree. With
    ``strict_locale`` (an explicitly named locale) a missing fixture file
    raises instead of noting, so a typo'd locale never reads as all-clean;
    an unreadable working-tree model is NOT a refusal in any mode: it falls
    that locale back to ALL of its rows with the reason in the payload,
    exactly like the base side (review round, finding 1), so a malformed
    model mid-template-edit can never silently deselect a whole locale's
    suite rows. stdout is never written here; the notes ride stderr through
    the CLI. ``totals.rows_selected`` counts DISTINCT selected utterances
    (it equals the sum of the payload ``rows`` lengths); ``rows_total``
    counts fixture entries.
    """
    directory = Path(fixtures_dir) if fixtures_dir is not None else fixture_vocab.default_fixtures_dir()
    notes: list[str] = []
    locales_out: dict[str, dict] = {}
    totals = {"rows_selected": 0, "rows_total": 0}
    for locale in locales:
        rows, skip_reason = fixture_vocab.load_fixture_rows(directory, locale)
        if skip_reason is not None:
            if strict_locale:
                raise ValueError(skip_reason)
            notes.append(skip_reason)
            rows = []
        rows_total = len(rows)
        try:
            new_raw = load_locale_model(locale, models_dir)
        except (OSError, ValueError) as err:
            fallback = _fallback_from_rows(
                rows, f"working-tree model unreadable: {err}", rows_total
            )
            locales_out[locale] = fallback.payload
            totals["rows_selected"] += fallback.rows_selected
            totals["rows_total"] += rows_total
            continue
        try:
            old_raw: dict | None = git_show_model(base, locale, repo_root=repo_root)
        except ModelMissingAtBase:
            old_raw = None
        except (GitShowError, ValueError) as err:
            # Environmental failures (no git binary) are not content
            # problems: OSError propagates to the CLI's refusal instead of
            # reading as an unreadable base (review round, finding 6).
            fallback = _fallback_from_rows(rows, f"model at base unreadable: {err}", rows_total)
            locales_out[locale] = fallback.payload
            totals["rows_selected"] += fallback.rows_selected
            totals["rows_total"] += rows_total
            continue
        result = select_locale(locale, old_raw, new_raw, rows, neighbours=neighbours)
        locales_out[locale] = result.payload
        totals["rows_selected"] += result.rows_selected
        totals["rows_total"] += rows_total
        if result.malformed:
            notes.append(
                f"  note [{locale}]: {result.malformed} fixture row(s) without a "
                "string utterance or expected_intent were skipped"
            )
    payload = {
        "schema": SELECTION_SCHEMA,
        "base": base,
        "locales": locales_out,
        "totals": totals,
    }
    return payload, notes


def render_select_text(payload: dict, neighbours_on: bool) -> str:
    """The deterministic one-line-per-locale select text form."""
    lines = [f"select base={payload['base']} neighbours={'on' if neighbours_on else 'off'}"]
    for locale in sorted(payload["locales"]):
        entry = payload["locales"][locale]
        status = entry["status"]
        if status == "unchanged":
            lines.append(f"  {locale}: unchanged")
        elif status == "fallback_all":
            lines.append(f"  {locale}: fallback_all rows={len(entry['rows'])}")
            lines.append(f"    reason: {entry['reason']}")
        else:
            lines.append(
                f"  {locale}: changed intents={len(entry['changed_intents'])} "
                f"neighbours={len(entry['neighbour_intents'])} rows={len(entry['rows'])}"
            )
            if entry.get("note"):
                lines.append(f"    note: {entry['note']}")
    totals = payload["totals"]
    lines.append(
        f"totals: rows_selected={totals['rows_selected']} rows_total={totals['rows_total']}"
    )
    return "\n".join(lines) + "\n"


# --------------------------------------------------------------- Part C


@dataclass(frozen=True)
class RowFilter:
    """A validated select payload with the kept rows indexed per locale.

    ``kept`` maps each locale to the normalized utterances the select run
    selected; a locale absent from the map (unchanged, or simply not part of
    the select run) keeps nothing.
    """

    kept: dict[str, frozenset[str]]


def row_filter_from_payload(doc: object, context: str = "rows file") -> RowFilter:
    """Validate a parsed select payload and index it for should_keep.

    Rejects anything that is not a select output of the right schema with a
    ValueError naming the problem under ``context``: the suite must abort on
    a wrong-schema filter file, never silently run everything or nothing.
    """
    if not isinstance(doc, dict):
        raise ValueError(f"{context}: top level must be a JSON object")
    schema = doc.get("schema")
    if isinstance(schema, bool) or schema != SELECTION_SCHEMA:
        raise ValueError(f"{context}: schema must be {SELECTION_SCHEMA}, got {schema!r}")
    locales = doc.get("locales")
    if not isinstance(locales, dict):
        raise ValueError(f"{context}: 'locales' must be an object keyed by locale")
    kept: dict[str, frozenset[str]] = {}
    for locale, entry in locales.items():
        if not isinstance(locale, str) or not isinstance(entry, dict):
            raise ValueError(f"{context}: locales.{locale!r} must be an object")
        status = entry.get("status")
        if status == "unchanged":
            continue
        rows = entry.get("rows")
        if not isinstance(rows, list) or not all(isinstance(row, str) for row in rows):
            raise ValueError(
                f"{context}: locales.{locale}: 'rows' must be a list of strings "
                f"(status {status!r})"
            )
        kept[locale] = frozenset(normalize_text(row) for row in rows)
    return RowFilter(kept=kept)


def load_row_filter(path: str | Path) -> RowFilter:
    """Load a ``select`` output file for NLU_ROW_FILTER.

    Raises OSError for an unreadable path and ValueError for a file that is
    not valid JSON or does not pass the payload schema check; the conftest
    hook turns either into a session abort with this exact message.
    """
    filter_path = Path(path)
    try:
        doc = json.loads(filter_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as err:
        raise ValueError(
            f"{filter_path}: not valid JSON ({err.msg} at line {err.lineno})"
        ) from err
    return row_filter_from_payload(doc, context=str(filter_path))


def should_keep(locale: str, utterance: str, selection: RowFilter) -> bool:
    """The pure decision behind the NLU_ROW_FILTER hook.

    Keep exactly when ``(locale, normalize_text(utterance))`` is listed in
    the selection; everything else is deselected, including every row of a
    locale the filter does not mention. Comparison rides the shared
    normalizer, so case, accent composition and whitespace fold away.
    """
    return normalize_text(utterance) in selection.kept.get(locale, frozenset())
