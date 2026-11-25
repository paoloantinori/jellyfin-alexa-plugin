"""The ER canary (JF-971): one catalog-liveness probe per wired catalog slot type.

The ledger identifies a session by the committed model sha and the live
interaction-model eTag; neither captures the state of the catalogs. The
2026-11-25 incident: all six interactionModel catalogs were deleted
Amazon-side, every catalog-slotted query read no-selection, and because the
model sha and eTag were unchanged those reds looked like model evidence and
the flips looked like trainer nondeterminism. The canary makes the TEST
evidence self-describing: before the rows and again at session end, ONE
profile-nlu probe per catalog type WIRED in that locale's committed model,
using a known value taken from a catalog snapshot, records whether the slot
resolved with ER_SUCCESS_MATCH on a catalog authority.

The reading rule is asymmetric (the nlu-debug-protocol skill, Gate 2): a
failed canary proves the catalogs are not usable and the session's reds on
catalog-slotted intents are not model evidence; a passed canary proves
liveness and propagation for THAT value only, since propagation is per
value. A session whose canary did not run is UNKNOWN: its pairs are simply
excluded from the same-model flip count, never treated as passed.

This module is the ONE definition, placed in nlu_static so both the suite
(tests/integration/conftest.py, which already imports this package for the
ledger) and the lab (scripts/nlu-lab/cli.py, whose _paths bootstrap puts
scripts/ on sys.path) import it without a new import edge. The only
cross-tree dependency is the ONE consideredIntents extractor in
tests/integration/smapi_client, imported lazily inside the parse so the
package itself stays dependency-free; unit tests inject their own
hypotheses extractor and never reach the network.

The record shape (a "catalog state block"):

    {
      "status": "ok" | "not_run",
      "reason": None or the reason string when not_run,
      "types": {
        "<TypeName>": {"value": ..., "intent": ..., "slot": ...,
                        "utterance": ..., "resolved": True/False/None,
                        "code": ..., "authority": ..., "error": None-or-str}
        or {"probeable": False, "reason": ...}
      }
    }

``resolved`` is True on an ER_SUCCESS_MATCH, False when the probe ran and
ER did not match (including a no-selection, the JF-684 signature), and None
when the probe call itself failed (unknown for that type). A block whose
status is "not_run" is UNKNOWN as a whole; its reason is always present,
because a silent skip would read as a passed canary in the history.
"""

from __future__ import annotations

import json
import os
import time
from pathlib import Path

from .effective_model import (
    is_catalog_type,
    load_locale_model,
    load_snapshot,
    resolve_locale_table,
)
from .normalize import iter_segments

DEFAULT_DELAY_S = 1.5

# Explicit not-run reasons. A not_run block ALWAYS carries one of these: a
# silent skip would make the history look like a passed canary.
REASON_DISABLED = "disabled by NLU_CANARY=0"
REASON_NO_SNAPSHOT = "no catalog snapshot (set NLU_CANARY_SNAPSHOT)"
REASON_UNWIRED_READER = "no catalog canary reader wired"
REASON_READER_FAILED = "catalog canary reader failed or returned no block"
REASON_SESSION_BROKEN = "catalog canary disabled for the rest of the session after a fault"
REASON_NO_SKILL_ID = "no skill id resolved"

# Sample placeholder shape: "{slot_name}", no nesting, matched exactly.
_PLACEHOLDER_START = "{"
_PLACEHOLDER_END = "}"


# ---------------------------------------------------------------------------
# Probe planning (pure; no network)
# ---------------------------------------------------------------------------


def _placeholders(sample: str) -> list[str]:
    """The placeholder names of one sample, in order of appearance.

    Reads normalize.iter_segments (the ONE placeholder walk, shared with
    the pattern and carrier layers), so the canary's sample segmentation
    can never drift from what the matcher sees. The name is the literal
    brace content, unpadded: the substitution below replaces the same
    exact ``{name}`` text, so a padded sample cleanly fails the exact
    match instead of half-matching.
    """
    return [name for kind, name in iter_segments(sample) if kind == "slot"]


def _sample_for_slot(intent_samples: tuple[str, ...], slot_name: str) -> str | None:
    """One sample of the intent whose only placeholder is the given slot.

    Slot-final samples win over mid-sample ones (a trailing value reads as
    the natural one-shot shape); among equals the declared sample order
    decides, so the plan is deterministic for a given model. A sample with
    any OTHER placeholder cannot carry the value cleanly (the other slots
    would have to be invented), so it is never a candidate.
    """
    slot_ref = _PLACEHOLDER_START + slot_name + _PLACEHOLDER_END
    best: str | None = None
    for sample in intent_samples:
        names = _placeholders(sample)
        if names != [slot_name]:
            continue
        if sample.rstrip().endswith(slot_ref):
            return sample  # slot-final: cannot be beaten
        if best is None:
            best = sample
    return best


def wired_catalog_hosts(
    locale: str,
    models_dir: str | Path | None = None,
    raw_model: dict | None = None,
) -> dict[str, list[tuple[str, str]]]:
    """The catalog-backed slot types WIRED in a locale's committed model.

    Returns type name -> ordered [(intent_name, slot_name)] for every
    intent whose slot declaration names a catalog-backed type
    (effective_model.is_catalog_type). A type the locale model never
    declares is not wired in that locale and gets no probe: the canary
    checks liveness per LOCALE, not per family.
    """
    model = raw_model if raw_model is not None else load_locale_model(locale, models_dir)
    hosts: dict[str, list[tuple[str, str]]] = {}
    for intent in model.get("intents", []):
        name = intent.get("name", "")
        for slot in intent.get("slots", []):
            type_name = slot.get("type", "")
            if is_catalog_type(type_name):
                hosts.setdefault(type_name, []).append((name, slot.get("name", "")))
    return hosts


def build_canary_plan(
    locale: str,
    snapshot: dict,
    models_dir: str | Path | None = None,
    raw_model: dict | None = None,
) -> list[dict]:
    """The ordered probe list for one locale: one entry per wired catalog type.

    Each entry is either a probeable plan ({type, value, intent, slot,
    utterance}) or a non-probeable note ({type, probeable: False, reason}).
    The value is the FIRST canonical value of the type in the snapshot's
    table for this locale (or the "*" fallback); a type missing from the
    snapshot, a type with no clean carrying sample, and a static type
    (never wired) are marked instead of forced.
    """
    model = raw_model if raw_model is not None else load_locale_model(locale, models_dir)
    hosts = wired_catalog_hosts(locale, raw_model=model)
    table = resolve_locale_table(snapshot, locale)
    intents_by_name = {
        intent.get("name"): intent for intent in model.get("intents", [])
    }
    plan: list[dict] = []
    for type_name in sorted(hosts):
        entries = table.get(type_name) if table else None
        if not entries or not isinstance(entries[0], dict) or not isinstance(
            entries[0].get("value"), str
        ):
            plan.append({
                "type": type_name,
                "probeable": False,
                "reason": "no snapshot value available",
            })
            continue
        value = entries[0]["value"]
        probe = None
        for intent_name, slot_name in hosts[type_name]:
            intent = intents_by_name.get(intent_name)
            if intent is None:
                continue
            sample = _sample_for_slot(tuple(intent.get("samples", [])), slot_name)
            if sample is not None:
                probe = {
                    "type": type_name,
                    "value": value,
                    "intent": intent_name,
                    "slot": slot_name,
                    "utterance": sample.replace(
                        _PLACEHOLDER_START + slot_name + _PLACEHOLDER_END, value
                    ),
                }
                break
        if probe is None:
            plan.append({
                "type": type_name,
                "probeable": False,
                "reason": "no committed sample can carry the value (single-slot only)",
            })
            continue
        plan.append(probe)
    return plan


# ---------------------------------------------------------------------------
# Block construction and comparison
# ---------------------------------------------------------------------------


def not_run_block(reason: str) -> dict:
    """The explicit not-run block; the reason is mandatory."""
    return {"status": "not_run", "reason": reason, "types": {}}


def canonical_state(block: dict) -> str:
    """The deterministic comparison form of one block (JSON, sorted keys).

    Equality of canonical forms is the ONLY state-equality test the stats
    and triage use, so incidental dict ordering can never make two equal
    states look different.
    """
    return json.dumps(block, sort_keys=True, ensure_ascii=False, separators=(",", ":"))


def block_is_ok(block) -> bool:
    """True for a parsed ok block; anything else (not_run, None, non-dict) is unknown."""
    return isinstance(block, dict) and block.get("status") == "ok"


def _probe_records(block) -> list[dict]:
    """The probe records of one block (the types a probe actually ran on)."""
    if not block_is_ok(block):
        return []
    return [
        record
        for record in block.get("types", {}).values()
        if isinstance(record, dict) and not record.get("probeable")
    ]


def _record_failed(record) -> bool:
    """The ONE unresolved-probe predicate: the probe RAN (probeable record)
    and ER did not match. A not-probeable typing carries no resolved key and
    a missing record is not a probe: neither is a failed probe, and resolved
    None (the probe call itself failed) is unknown, not failed.
    """
    return isinstance(record, dict) and record.get("resolved") is False


def _failed_values_in_block(block) -> list:
    """The values whose probes failed inside one block (all types)."""
    return [
        record.get("value") for record in _probe_records(block) if _record_failed(record)
    ]


def _has_unknown_probe(block) -> bool:
    """True when any probe in the block came back UNKNOWN (resolved None).

    A transient probe error (a SMAPI 500, a hung CLI) must never read as a
    catalog verdict in either direction: the block stays ok (the other
    types were verified) but the session's STATE is not comparable while an
    endcap carries an unknown, so neither the stats pair rule nor the
    triage changed-rule can mistake one errored probe for a catalog change.
    """
    return any(record.get("resolved") is None for record in _probe_records(block))


def entry_stable_canonical(entry) -> str | None:
    """The locale entry's ONE catalog state for the session, or None.

    ``entry`` is a load_sessions per-locale dict. None when either the
    start or the end block is missing (an incomplete session, a session
    recorded before JF-971, or a canary that never ran), when either block
    is not ok, when the two differ (the catalog state changed DURING the
    session, so no single state describes it), when any probeable entry
    shows resolved False in either block (the Gate 2 rule: a session where
    a canary failed is a catalog-state session, its reds are not model
    evidence and its flips are not trainer noise, so a failing session has
    NO comparable state even when the next session fails identically), when
    any probe came back UNKNOWN (a transient error proves nothing, in
    neither direction), or when the blocks prove nothing (types present but
    every one not-probeable: a silent pass would be exactly the shape the
    mandatory not_run reason bans). A block with NO types at all stays
    comparable: a locale that wires no catalog types cannot be catalog-dead.
    A comparable state certifies liveness for ITS values only, and the
    stats stay honest by excluding pairs whose states are not proven
    equal-and-clean.
    """
    if not isinstance(entry, dict):
        return None
    start = entry.get("catalog_state_start")
    end = entry.get("catalog_state_end")
    if not block_is_ok(start) or not block_is_ok(end):
        return None
    start_canon = canonical_state(start)
    if start_canon != canonical_state(end):
        return None
    if any(_failed_values_in_block(block) for block in (start, end)):
        return None
    if any(_has_unknown_probe(block) for block in (start, end)):
        return None
    # vacuous evidence: the locale declares catalog types but none was
    # probeable at either endcap, so equality here proves nothing
    for block in (start, end):
        types = block.get("types", {})
        if types and not _probe_records(block):
            return None
    return start_canon


def entry_state_changed(entry) -> bool | None:
    """True when both end blocks exist, are ok, and differ; None when unknown.

    None also when any probe in either block came back UNKNOWN: one
    transient probe error must never read as a catalog change.
    """
    if not isinstance(entry, dict):
        return None
    start = entry.get("catalog_state_start")
    end = entry.get("catalog_state_end")
    if not block_is_ok(start) or not block_is_ok(end):
        return None
    if any(_has_unknown_probe(block) for block in (start, end)):
        return None
    return canonical_state(start) != canonical_state(end)


def entry_type_failures(entry, type_name: str) -> list[dict]:
    """The probes of one type that ran and did NOT resolve, start or end.

    Returns a list of {value, phase} (phase "start" or "end"); empty when
    the type passed everywhere it was probed or was never probed (unknown
    is never read as failed).
    """
    if not isinstance(entry, dict):
        return []
    failures: list[dict] = []
    for phase, key in (("start", "catalog_state_start"), ("end", "catalog_state_end")):
        block = entry.get(key)
        if not block_is_ok(block):
            continue
        record = block.get("types", {}).get(type_name)
        if _record_failed(record):
            failures.append({"value": record.get("value"), "phase": phase})
    return failures


# ---------------------------------------------------------------------------
# Execution (the only network-touching path; profile_fn is injected)
# ---------------------------------------------------------------------------


def _default_hypotheses(response: dict) -> list[dict]:
    """The ONE consideredIntents extractor, resolved lazily.

    tests/integration/smapi_client is the extractor's home (the suite and
    the lab both have that directory on sys.path); the import sits inside
    this function so importing nlu_static alone never pulls it in. A
    missing module is an environment error with a name, not a silent
    degradation.
    """
    try:
        from smapi_client import considered_hypotheses  # noqa: PLC0415 - lazy on purpose
    except ImportError as err:
        raise RuntimeError(
            "the canary's consideredIntents extractor lives in "
            "tests/integration/smapi_client, which is not importable here; "
            "put tests/integration on sys.path or inject hypotheses_fn"
        ) from err
    return considered_hypotheses(response)


def _evaluate_probe(response, intent: str, slot: str, hypotheses_fn) -> dict:
    """Whether the canary value resolved with ER on a catalog authority.

    The probe intent can appear MULTIPLE times in consideredIntents with
    different slot segmentations (the extractor's documented shape), so the
    scan walks EVERY hypothesis matching the intent and resolves True on the
    first ER_SUCCESS_MATCH found anywhere. Only when no matching hypothesis
    carries a match does the probe read False: False with a code is an
    explicit ER no-match, False with a null code is a no-selection or an
    unfilled slot (the JF-684 dead-catalog signature either way). resolved
    None never comes back from here; caller-level failures carry it.
    """
    first_seen: dict | None = None
    for hypothesis in hypotheses_fn(response):
        if hypothesis.get("intent") != intent:
            continue
        for record in hypothesis.get("er", {}).get(slot, []):
            if record.get("code") == "ER_SUCCESS_MATCH":
                return {
                    "resolved": True,
                    "code": record.get("code"),
                    "authority": record.get("authority"),
                }
            if first_seen is None:
                first_seen = {
                    "resolved": False,
                    "code": record.get("code"),
                    "authority": record.get("authority"),
                }
    if first_seen is not None:
        return first_seen
    return {"resolved": False, "code": None, "authority": None}


def run_canary(
    plan: list[dict],
    profile_fn,
    delay: float | None = None,
    hypotheses_fn=None,
) -> dict:
    """Run the plan's probes through profile_fn and return the ok block.

    profile_fn takes the utterance and returns the raw profile-nlu response
    dict (SmapiClient.profile_nlu in the suite, smapi_ops.profile_row in
    the lab). Every pair of consecutive calls is spaced by ``delay`` (None
    = SMAPI_DELAY env, repo default 1.5) exactly like run_battery_rows, so
    the canary never busts the SMAPI rate limit. A probe whose profile call
    raises or returns a non-dict keeps that type UNKNOWN (resolved None
    with the error string) and never aborts the block: one dead type must
    not blind the others.
    """
    if delay is None:
        delay = float(os.environ.get("SMAPI_DELAY", str(DEFAULT_DELAY_S)))
    if hypotheses_fn is None:
        hypotheses_fn = _default_hypotheses
    types: dict = {}
    first_call = True
    for probe in plan:
        type_name = probe["type"]
        if not probe.get("probeable", True):
            types[type_name] = {
                "probeable": False,
                "reason": probe.get("reason", "not probeable"),
            }
            continue
        if not first_call:
            time.sleep(delay)
        first_call = False
        record: dict = {
            "value": probe["value"],
            "intent": probe["intent"],
            "slot": probe["slot"],
            "utterance": probe["utterance"],
            "resolved": None,
            "code": None,
            "authority": None,
            "error": None,
        }
        response = None
        try:
            response = profile_fn(probe["utterance"])
        except Exception as err:  # noqa: BLE001 - one dead type must not blind the others
            record["error"] = f"{type(err).__name__}: {err}"
        if not isinstance(response, dict):
            if record["error"] is None:
                record["error"] = "profile call returned a non-dict response"
        else:
            verdict = _evaluate_probe(response, probe["intent"], probe["slot"], hypotheses_fn)
            record["resolved"] = verdict["resolved"]
            record["code"] = verdict["code"]
            record["authority"] = verdict["authority"]
        types[type_name] = record
    return {"status": "ok", "reason": None, "types": types}


# ---------------------------------------------------------------------------
# Environment plumbing (ONE path shared by the suite reader and the lab)
# ---------------------------------------------------------------------------


def canary_enabled() -> bool:
    """False only when NLU_CANARY is exactly "0"; any other value enables."""
    return os.environ.get("NLU_CANARY", "") != "0"


def snapshot_or_reason() -> tuple[dict | None, str | None]:
    """(snapshot, None) from NLU_CANARY_SNAPSHOT, or (None, explicit reason).

    The snapshot format is the JF-954 snapshot (effective_model.load_snapshot
    validates it); a missing variable and an unreadable file both return an
    explicit reason so the caller records not_run instead of skipping.
    """
    path = os.environ.get("NLU_CANARY_SNAPSHOT", "")
    if not path:
        return None, REASON_NO_SNAPSHOT
    try:
        return load_snapshot(path), None
    except (OSError, ValueError) as err:
        return None, f"catalog snapshot unreadable: {err}"


def environment_block(
    locale: str,
    profile_fn,
    delay: float | None = None,
) -> dict:
    """The catalog state block for one locale under the current environment.

    The ONE env path both the suite reader and the lab call: NLU_CANARY=0
    gives the explicit disabled block, a missing or unreadable snapshot the
    explicit no-snapshot block, and any planning or execution fault the
    explicit fault block. This function never raises, so the ledger's
    never-change-a-test-outcome contract holds by construction.
    """
    if not canary_enabled():
        return not_run_block(REASON_DISABLED)
    snapshot, reason = snapshot_or_reason()
    if snapshot is None:
        return not_run_block(reason or REASON_NO_SNAPSHOT)
    try:
        plan = build_canary_plan(locale, snapshot)
        return run_canary(plan, profile_fn, delay=delay)
    except Exception as err:  # noqa: BLE001 - the canary must never fail its caller
        return not_run_block(f"catalog canary failed: {err!r}")
