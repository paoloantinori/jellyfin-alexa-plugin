"""Automatic triage report (JF-954.6): one verdict per red NLU row.

After a live suite session recorded in the ledger (JF-954.5), this module
classifies every red row (outcome ``fail`` or ``error``) with a FIXED
precedence of eight verdicts, groups the rows into families, and renders a
markdown or JSON report. The verdicts are necessary conditions and
recorded history, never routing predictions.

The eight verdicts, in the exact precedence (first match wins):

1. ``INFRA``: outcome is ``error``; the probe failed, nothing can be said
   about routing.
2. ``UNSTABLE_BUILD``: the locale's live model identity changed during the
   session (``rebuilt_during_run``), so the row must be re-probed.
3. ``CATALOG_STATE`` (JF-971): the row's expected intents consume a
   catalog-backed slot type and the session's ER canary either FAILED for
   one of those types (the value was probed and ER did not match) or
   CHANGED between session start and end. Per the nlu-debug-protocol Gate
   2 reading rule, the canary is asymmetric: a failed canary proves the
   catalogs are not usable, so the session's catalog-slotted reds are not
   model evidence; a passed canary proves liveness for THAT value only,
   and it never fires this rule. An unknown state (canary not run) never
   fires either.
4. ``FIXTURE_VOCAB``: the fixture-vocabulary check (JF-954.3) reports
   ``ABSENT`` or ``FIXTURE_SLOT_UNKNOWN`` for one of the row's pinned
   slots. Per the JF-954.3 precision: in committed mode a plain ``ABSENT``
   on a CATALOG type means only "not in the committed seed", so it does
   NOT fire; ``ABSENT`` fires there only on STATIC types, and always in
   snapshot mode. ``ABSENT_IN_LIBRARY_SEED_ONLY`` never fires the rule.
5. ``KNOWN_FLAKY``: prior sessions show at least ``--flaky-min-flips``
   same-model flips for the row key within the history window. A row with
   fewer than two prior runs is never KNOWN_FLAKY (``history:
   insufficient``).
6. ``CONTESTED``: the matcher (JF-954.4) classifies the row CONTESTED or
   NO_SAMPLE_MATCH and the row's ``actual_intent`` is one of the rivals.
7. ``TOKEN_COLLISION``: a ``CARRIER_VS_VALUE`` finding (JF-954.2) exists
   in the locale whose carrying intent is one of the row's expected
   intents, the row's ``actual_intent`` uses the colliding type, and the
   colliding entry occurs in the utterance.
8. ``SYSTEMATIC_CANDIDATE``: none of the above; the rows a human must
   look at first.

ja-JP rows skip rules 6 and 7 (no whitespace tokenization) with an
explicit note in their detail; they can still receive every other verdict.

Every row carries its raw signals regardless of verdict (``vocab_status``,
``match_classification``, ``rivals``, ``flips_same_model``,
``history_runs``, ``rebuilt_during_run``, ``failure_kind``,
``catalog_types``, ``catalog_changed``, ``catalog_failed``), so a reader
can see why a lower rule never got a chance. The precedence lives as ONE
ordered list of (verdict, predicate) pairs in ``_first_verdict``; do not
scatter it across modules.
"""

from __future__ import annotations

import json
from dataclasses import dataclass

from nlu_static import catalog_canary, fixture_vocab, ledger, matcher, token_collisions
from nlu_static.effective_model import (
    DEFAULT_MODELS_DIR,
    EffectiveModel,
    build_effective_model,
    is_catalog_type,
    load_locale_model,
)
from nlu_static.normalize import (
    contains_contiguous,
    is_whitespace_tokenized,
    normalize_text,
    tokenize,
)

TRIAGE_SCHEMA = 1

V_INFRA = "INFRA"
V_UNSTABLE_BUILD = "UNSTABLE_BUILD"
V_CATALOG_STATE = "CATALOG_STATE"
V_FIXTURE_VOCAB = "FIXTURE_VOCAB"
V_KNOWN_FLAKY = "KNOWN_FLAKY"
V_CONTESTED = "CONTESTED"
V_TOKEN_COLLISION = "TOKEN_COLLISION"
V_SYSTEMATIC = "SYSTEMATIC_CANDIDATE"

VERDICT_ORDER: tuple[str, ...] = (
    V_INFRA,
    V_UNSTABLE_BUILD,
    V_CATALOG_STATE,
    V_FIXTURE_VOCAB,
    V_KNOWN_FLAKY,
    V_CONTESTED,
    V_TOKEN_COLLISION,
    V_SYSTEMATIC,
)

RED_OUTCOMES = frozenset({ledger.OUTCOME_FAIL, ledger.OUTCOME_ERROR})

DEFAULT_HISTORY = 10
DEFAULT_FLAKY_MIN_FLIPS = 1

# The severity order behind the single ``vocab_status`` signal: the worst
# per-slot status of the row wins, so a reader sees the status that could
# have fired rule 3.
_VOCAB_SEVERITY: tuple[str, ...] = (
    fixture_vocab.STATUS_FIXTURE_SLOT_UNKNOWN,
    fixture_vocab.STATUS_ABSENT,
    fixture_vocab.STATUS_NO_ENTRY_IN_UTTERANCE,
    fixture_vocab.STATUS_ABSENT_IN_LIBRARY_SEED_ONLY,
    fixture_vocab.STATUS_PRESENT,
    fixture_vocab.STATUS_NOT_APPLICABLE,
)

DETAIL_HISTORY_INSUFFICIENT = "history: insufficient"
DETAIL_JA_SKIPPED = "static signals skipped: ja-JP"

LIMITS_PARAGRAPH = (
    "These verdicts describe necessary conditions and recorded history; "
    "they do not predict how Amazon's trainer routes an utterance. "
    "Confirm any conclusion with profile-nlu, simulate-skill or a device "
    "test before changing a fixture or a model."
)


# ---------------------------------------------------------------- contexts


@dataclass
class LocaleContext:
    """The per-locale state computed ONCE, not per row.

    ``model_ok`` is False after a failed effective-model build; the SKIP
    reason is carried in ``skip_reason`` (printed by the CLI) and the
    matcher and vocabulary signals of the locale's rows stay None so rules
    3, 5 and 6 cannot fire on an unreadable model.
    """

    locale: str
    model: EffectiveModel | None
    committed_model: EffectiveModel | None
    prepared: matcher.PreparedMatcher | None
    slot_types_by_intent: dict[str, set[str]]
    vocab_by_row: dict[tuple[str, str], list[dict]]
    collisions: tuple[token_collisions.CollisionFinding, ...]
    model_ok: bool
    skip_reason: str | None


class TriageContext:
    """Everything a triage run shares: locale contexts and the history stats."""

    def __init__(
        self,
        chosen: dict,
        history_sessions: list[dict],
        snapshot: dict | None,
        flaky_min_flips: int,
        models_dir=DEFAULT_MODELS_DIR,
    ):
        self.chosen = chosen
        self.snapshot = snapshot
        self.flaky_min_flips = flaky_min_flips
        self.models_dir = models_dir
        self.locales: dict[str, LocaleContext] = {}
        self.skip_lines: list[str] = []
        stats = ledger.compute_stats(history_sessions)
        self.history_by_key = {
            (row["locale"], row["utterance"]): row for row in stats["rows"]
        }

    # -- per-locale lazy build -------------------------------------------

    def locale_context(self, locale: str) -> LocaleContext:
        cached = self.locales.get(locale)
        if cached is not None:
            return cached
        ctx = self._build_locale(locale)
        self.locales[locale] = ctx
        return ctx

    def _build_locale(self, locale: str) -> LocaleContext:
        model = None
        committed_model = None
        prepared = None
        vocab_by_row: dict[tuple[str, str], list[dict]] = {}
        collisions: tuple = ()
        try:
            model = build_effective_model(
                locale, snapshot=self.snapshot, models_dir=self.models_dir
            )
            if self.snapshot is not None:
                committed_model = build_effective_model(
                    locale, models_dir=self.models_dir
                )
        except (OSError, ValueError) as err:
            reason = f"SKIP {locale}: effective model unavailable ({err})"
            self.skip_lines.append(reason)
            return LocaleContext(
                locale=locale,
                model=None,
                committed_model=None,
                prepared=None,
                slot_types_by_intent={},
                vocab_by_row={},
                collisions=(),
                model_ok=False,
                skip_reason=reason,
            )
        prepared = matcher.PreparedMatcher(model)
        slot_types_by_intent: dict[str, set[str]] = {}
        for intent in model.intents:
            slot_types_by_intent[intent.name] = {
                slot.type for slot in intent.slots if slot.type
            }
        if is_whitespace_tokenized(locale):
            # The collision lint must see the SAME effective model the slot
            # types above come from, snapshot included: a snapshot-introduced
            # collision has to fire rule 6 and a snapshot-removed one must
            # not. raw_model skips the second file read of the same JSON.
            raw = load_locale_model(locale, self.models_dir)
            report = token_collisions.analyze_locale(
                locale, snapshot=self.snapshot, models_dir=self.models_dir, raw_model=raw
            )
            collisions = tuple(
                finding
                for finding in report.findings
                if finding.kind == token_collisions.KIND_CARRIER_VS_VALUE
            )
        return LocaleContext(
            locale=locale,
            model=model,
            committed_model=committed_model,
            prepared=prepared,
            slot_types_by_intent=slot_types_by_intent,
            vocab_by_row=vocab_by_row,
            collisions=collisions,
            model_ok=True,
            skip_reason=None,
        )

    def vocab_slots(self, locale: str, utterance: str, expected_intent: str, expected_slots) -> list[dict]:
        """The per-slot fixture-vocabulary payloads of one ledger row.

        The row shape fed to ``check_fixture_rows`` is built from the
        LEDGER record's ``expected_slots`` (stored verbatim by the
        writer); no fixture file is ever opened here.
        """
        ctx = self.locale_context(locale)
        if not ctx.model_ok or not isinstance(expected_slots, dict):
            return []
        # The key must include the pins: two rows can share utterance and
        # intent while pinning different values, and the payloads depend on
        # all three.
        key = (utterance, expected_intent, json.dumps(expected_slots, sort_keys=True))
        cached = ctx.vocab_by_row.get(key)
        if cached is None:
            results = fixture_vocab.check_fixture_rows(
                ctx.model,
                [
                    {
                        "utterance": utterance,
                        "expected_intent": expected_intent,
                        "expected_slots": expected_slots,
                    }
                ],
                committed_model=ctx.committed_model,
                snapshot=self.snapshot,
            )
            cached = [result.payload() for result in results]
            ctx.vocab_by_row[key] = cached
        return cached


def worst_vocab_status(vocab_slots: list[dict]) -> str | None:
    """The highest-severity per-slot status, or None when no slot was checked."""
    worst: str | None = None
    for payload in vocab_slots:
        status = payload.get("status")
        if worst is None or _severity(status) < _severity(worst):
            worst = status
    return worst


def expected_intents_of(raw_expected: str) -> set[str]:
    """The expected intent names of a row, split on the ``|`` alternatives.

    ONE definition: the _RowState property and the catalog-type scan read
    the same splitter, so the two rules can never disagree about what a
    row expects.
    """
    return {name.strip() for name in (raw_expected or "").split("|") if name.strip()}


def _severity(status: str) -> int:
    try:
        return _VOCAB_SEVERITY.index(status)
    except ValueError:
        # A status the severity table does not know (a renamed or new
        # fixture_vocab status) must be LOUD, not silent: rank it as the
        # worst bucket so it wins worst_vocab_status and the reader sees it.
        return -1


# ---------------------------------------------------------------- row triage


def triage_row(row: dict, ctx: TriageContext) -> dict:
    """Classify one red ledger row; the report's one-row unit.

    Returns the row payload with the verdict, its detail list and every
    raw signal. The precedence is the ordered RULES list in
    ``_first_verdict`` and nowhere else.
    """
    locale = str(row.get("locale") or "")
    utterance = str(row.get("utterance") or "")
    expected_intent = str(row.get("expected_intent") or "")
    expected_slots = row.get("expected_slots")
    actual_intent = row.get("actual_intent")

    locale_entry = ctx.chosen.get("locales", {}).get(locale, {})
    rebuilt = ledger.rebuilt_during_run(locale_entry)
    history = ctx.history_by_key.get((locale, normalize_text(utterance)))
    history_runs = history["runs"] if history else 0
    flips_same_model = history["flips_same_model"] if history else 0

    is_ja = not is_whitespace_tokenized(locale)
    lctx = ctx.locale_context(locale)
    if lctx.model_ok and not is_ja:
        classification = matcher.classify_fixture_row(
            {"utterance": utterance, "expected_intent": expected_intent},
            lctx.model,
            lctx.prepared,
        )
        match_classification = classification["classification"]
        rivals = classification["rivals"]
        rival_flag = classification["rival_at_least_as_specific"]
    else:
        match_classification = None
        rivals = []
        rival_flag = False

    vocab_slots = ctx.vocab_slots(locale, utterance, expected_intent, expected_slots)

    # JF-971: the session's catalog canary signals. The types come from the
    # expected intents' DECLARED slot types (the effective model), the
    # failure and change signals from the chosen session's blocks; unknown
    # (no blocks, not_run) reads as no signal, never as passed.
    catalog_types = sorted(
        {
            slot_type
            for intent_name in expected_intents_of(expected_intent)
            for slot_type in lctx.slot_types_by_intent.get(intent_name, set())
            if is_catalog_type(slot_type)
        }
    )
    catalog_changed = catalog_canary.entry_state_changed(locale_entry)
    catalog_failed = [
        {"type": type_name, **failure}
        for type_name in catalog_types
        for failure in catalog_canary.entry_type_failures(locale_entry, type_name)
    ]

    signals = {
        "locale": locale,
        "utterance": utterance,
        "expected_intent": expected_intent,
        "expected_slots": expected_slots if isinstance(expected_slots, dict) else {},
        "actual_intent": actual_intent,
        "outcome": row.get("outcome"),
        "failure_kind": row.get("failure_kind"),
        "vocab_status": worst_vocab_status(vocab_slots),
        "vocab_slots": vocab_slots,
        "match_classification": match_classification,
        "rivals": rivals,
        "rival_at_least_as_specific": rival_flag,
        "flips_same_model": flips_same_model,
        "history_runs": history_runs,
        "rebuilt_during_run": rebuilt,
        "catalog_types": catalog_types,
        "catalog_changed": catalog_changed,
        "catalog_failed": catalog_failed,
    }

    state = _RowState(
        signals=signals,
        locale_ctx=lctx,
        flaky_min_flips=ctx.flaky_min_flips,
        is_ja=is_ja,
    )
    verdict, verdict_details = _first_verdict(state)
    details = list(verdict_details)
    if history_runs < 2:
        details.append(DETAIL_HISTORY_INSUFFICIENT)
    if is_ja:
        details.append(DETAIL_JA_SKIPPED)
    signals["verdict"] = verdict
    signals["detail"] = details
    return signals


@dataclass
class _RowState:
    """The one-row view the predicates read; built once per row by triage_row."""

    signals: dict
    locale_ctx: LocaleContext
    flaky_min_flips: int
    is_ja: bool

    @property
    def expected_intents(self) -> set[str]:
        return expected_intents_of(self.signals["expected_intent"])


def _first_verdict(state: _RowState) -> tuple[str, list[str]]:
    """THE precedence: one ordered (verdict, predicate) list, first match wins."""
    rules = (
        (V_INFRA, _pred_infra),
        (V_UNSTABLE_BUILD, _pred_unstable_build),
        (V_CATALOG_STATE, _pred_catalog_state),
        (V_FIXTURE_VOCAB, _pred_fixture_vocab),
        (V_KNOWN_FLAKY, _pred_known_flaky),
        (V_CONTESTED, _pred_contested),
        (V_TOKEN_COLLISION, _pred_token_collision),
        (V_SYSTEMATIC, _pred_systematic),
    )
    for verdict, predicate in rules:
        fired, details = predicate(state)
        if fired:
            return verdict, details
    return V_SYSTEMATIC, []


def _pred_infra(state: _RowState) -> tuple[bool, list[str]]:
    fired = state.signals["outcome"] == ledger.OUTCOME_ERROR
    details = [f"failure_kind={state.signals['failure_kind']}"] if fired else []
    return fired, details


def _pred_unstable_build(state: _RowState) -> tuple[bool, list[str]]:
    fired = state.signals["rebuilt_during_run"] is True
    return fired, ["the locale's live model changed during the session"] if fired else []


def _pred_catalog_state(state: _RowState) -> tuple[bool, list[str]]:
    """JF-971: a failed or changed canary disqualifies the session's catalog reds.

    Fires when one of the row's catalog types was PROBED and did not
    resolve (start or end), or when the session's catalog state changed
    between start and end (the row ran under more than one catalog state).
    An unknown state (blocks missing, not_run, probe errored) never fires:
    unknown is not evidence in either direction.
    """
    failed = state.signals["catalog_failed"]
    changed = state.signals["catalog_changed"]
    if not failed and changed is not True:
        return False, []
    details = [
        f"catalog canary: '{entry['type']}' unresolved "
        f"(value {entry['value']!r}, {entry['phase']})"
        for entry in failed
    ]
    if changed is True:
        details.append("catalog canary: state changed between session start and end")
    return True, details


def _pred_fixture_vocab(state: _RowState) -> tuple[bool, list[str]]:
    for payload in state.signals["vocab_slots"]:
        status = payload.get("status")
        slot = payload.get("slot")
        if status == fixture_vocab.STATUS_FIXTURE_SLOT_UNKNOWN:
            return True, [f"slot '{slot}': declared by no expected intent"]
        if status == fixture_vocab.STATUS_ABSENT:
            if payload.get("mode") == "snapshot":
                return True, [f"slot '{slot}': absent in live catalog"]
            if payload.get("type_kind") == "STATIC":
                return True, [f"slot '{slot}': absent in committed seed"]
            # Committed-mode ABSENT on a CATALOG type means only "not in
            # the committed seed" (the JF-954.3 precision): it never fires.
    return False, []


def _pred_known_flaky(state: _RowState) -> tuple[bool, list[str]]:
    signals = state.signals
    if signals["history_runs"] < 2:
        return False, []
    fired = signals["flips_same_model"] >= state.flaky_min_flips
    details = (
        [
            f"flips_same_model={signals['flips_same_model']} "
            f"over {signals['history_runs']} prior run(s)"
        ]
        if fired
        else []
    )
    return fired, details


def _pred_contested(state: _RowState) -> tuple[bool, list[str]]:
    if state.is_ja:
        return False, []
    signals = state.signals
    classification = signals["match_classification"]
    if classification not in ("CONTESTED", "NO_SAMPLE_MATCH"):
        return False, []
    actual = signals["actual_intent"]
    for rival in signals["rivals"]:
        if rival.get("intent") == actual:
            detail = (
                f"lost to {actual} sample \"{rival.get('sample')}\" "
                f"(specificity {rival.get('specificity')}, "
                f"rival at least as specific: "
                f"{'true' if signals['rival_at_least_as_specific'] else 'false'})"
            )
            return True, [detail]
    return False, []


def _pred_token_collision(state: _RowState) -> tuple[bool, list[str]]:
    if state.is_ja:
        return False, []
    signals = state.signals
    actual = signals["actual_intent"]
    if not actual:
        return False, []
    actual_types = state.locale_ctx.slot_types_by_intent.get(actual, set())
    utterance_tokens = tuple(tokenize(signals["utterance"]))
    for finding in state.locale_ctx.collisions:
        if finding.intent not in state.expected_intents:
            continue
        type_name = finding.type_names[0]
        if type_name not in actual_types:
            continue
        entry_tokens = tuple(tokenize(finding.entry))
        if not entry_tokens or not contains_contiguous(utterance_tokens, entry_tokens):
            continue
        return True, [f"token collision: {finding.identity()}"]
    return False, []


def _pred_systematic(state: _RowState) -> tuple[bool, list[str]]:
    return True, []


# ---------------------------------------------------------------- session


def choose_session(sessions: list[dict], session_arg: str | None) -> tuple[dict, str]:
    """Resolve the --session argument to (session, completeness note).

    No argument selects the latest COMPLETE session; ``latest`` accepts an
    incomplete one (the report header then says so); anything else is a
    session_id lookup. Raises ValueError for an empty directory, a
    complete-session-free directory and an unknown id.
    """
    if not sessions:
        raise ValueError("no ledger session found in the directory")
    if session_arg is None:
        complete = [s for s in sessions if s["complete"]]
        if not complete:
            raise ValueError(
                "no complete session in the ledger directory; "
                "pass --session latest to analyze an incomplete one"
            )
        return complete[-1], "complete"
    if session_arg == "latest":
        chosen = sessions[-1]
        return chosen, ("complete" if chosen["complete"] else "incomplete")
    for session in sessions:
        if session["session_id"] == session_arg:
            note = "complete" if session["complete"] else "incomplete"
            return session, note
    raise ValueError(f"unknown session id: {session_arg}")


def triage(
    sessions: list[dict],
    session_arg: str | None = None,
    snapshot: dict | None = None,
    history: int = DEFAULT_HISTORY,
    flaky_min_flips: int = DEFAULT_FLAKY_MIN_FLIPS,
    models_dir=DEFAULT_MODELS_DIR,
) -> dict:
    """The full report payload for one session; deterministic for the same inputs.

    Raises ValueError for a session selection problem and for a history
    window below 1 (the --last invariant of the ledger stats: a zero or
    negative window would silently disable the KNOWN_FLAKY rule). History
    sessions are the ledger sessions BEFORE the chosen one (the ledger's
    own started, session_id order), restricted to the last ``history`` of
    them.
    """
    if int(history) < 1:
        raise ValueError("--history must be a positive integer")
    chosen, completeness = choose_session(sessions, session_arg)
    index = sessions.index(chosen)
    history_sessions = sessions[max(0, index - history) : index]
    ctx = TriageContext(
        chosen=chosen,
        history_sessions=history_sessions,
        snapshot=snapshot,
        flaky_min_flips=flaky_min_flips,
        models_dir=models_dir,
    )

    red_rows = [
        row for row in chosen["rows"] if row.get("outcome") in RED_OUTCOMES
    ]
    rows = [triage_row(row, ctx) for row in red_rows]
    rows.sort(key=_row_sort_key)
    families = build_families(rows)
    verdict_counts = {verdict: 0 for verdict in VERDICT_ORDER}
    for row in rows:
        verdict_counts[row["verdict"]] += 1
    return {
        "schema": TRIAGE_SCHEMA,
        "session": ledger.session_payload(chosen),
        "session_completeness": completeness,
        "model_mode": "snapshot" if snapshot is not None else "committed",
        "snapshot_producer": (
            snapshot.get("producer") if isinstance(snapshot, dict) else None
        ),
        "history_sessions": len(history_sessions),
        "history_window": history,
        "flaky_min_flips": flaky_min_flips,
        "totals": {
            "rows": len(chosen["rows"]),
            "red_rows": len(rows),
            "verdicts": verdict_counts,
        },
        "skip_lines": sorted(ctx.skip_lines),
        "families": families,
        "rows": rows,
    }


def _row_sort_key(row: dict):
    return (
        row["locale"],
        row["utterance"],
        row["expected_intent"],
        row["actual_intent"] or "",
        row["verdict"],
    )


def family_key(row: dict) -> tuple[str, str, str, str]:
    """(language prefix, expected_intent, actual_intent, verdict)."""
    language = row["locale"].split("-", 1)[0]
    return (language, row["expected_intent"], row["actual_intent"] or "", row["verdict"])


def build_families(rows: list[dict]) -> list[dict]:
    """Group rows by family key; sorted by size descending, then by key."""
    grouped: dict[tuple[str, str, str, str], list[dict]] = {}
    for row in rows:
        grouped.setdefault(family_key(row), []).append(row)
    families = []
    for key in sorted(grouped, key=lambda k: (-len(grouped[k]), k)):
        members = sorted(grouped[key], key=_row_sort_key)
        families.append(
            {
                "language": key[0],
                "expected_intent": key[1],
                "actual_intent": key[2],
                "verdict": key[3],
                "size": len(members),
                "locales": sorted({row["locale"] for row in members}),
                "rows": members,
            }
        )
    return families


# ---------------------------------------------------------------- markdown


def _cell(text) -> str:
    """One markdown table cell: pipes and newlines escaped."""
    return str(text).replace("|", "\\|").replace("\n", " ").strip()


def render_markdown(payload: dict) -> str:
    """The md report: header, Look at these first, one section per other verdict, limits."""
    session = payload["session"]
    verdict_counts = payload["totals"]["verdicts"]
    lines = [
        "# NLU triage report",
        f"session: {session['session_id']} ({payload['session_completeness']})",
        f"git head: {session['git_head'] or 'unknown'}",
    ]
    if payload["model_mode"] == "snapshot":
        producer = payload["snapshot_producer"] or "unnamed"
        lines.append(f"model mode: snapshot (producer: {producer})")
    else:
        lines.append(
            "model mode: committed (re-run with a snapshot to answer "
            "the live-catalog question)"
        )
    lines.append(
        f"rows: {payload['totals']['rows']} total, "
        f"{payload['totals']['red_rows']} red "
        f"(history window: {payload['history_sessions']} prior session(s), "
        f"flaky threshold: {payload['flaky_min_flips']})"
    )
    counts = " ".join(f"{v}={verdict_counts[v]}" for v in VERDICT_ORDER)
    lines.append(f"verdicts: {counts}")
    for line in payload["skip_lines"]:
        lines.append(line)

    by_verdict: dict[str, list[dict]] = {
        verdict: [] for verdict in VERDICT_ORDER
    }
    for family in payload["families"]:
        by_verdict[family["verdict"]].append(family)

    def family_block(family: dict) -> list[str]:
        key = (
            f"{family['language']} | {family['expected_intent']} | "
            f"{family['actual_intent'] or '-'} | {family['verdict']}"
        )
        block = [f"### {key} ({family['size']} rows)"]
        block.append("locales: " + ", ".join(family["locales"]))
        block.append(
            "| locale | utterance | expected | actual | detail |"
        )
        block.append("|---|---|---|---|---|")
        for row in family["rows"]:
            block.append(
                "| {locale} | {utterance} | {expected} | {actual} | {detail} |".format(
                    locale=_cell(row["locale"]),
                    utterance=_cell(row["utterance"]),
                    expected=_cell(row["expected_intent"]),
                    actual=_cell(row["actual_intent"] or "-"),
                    detail=_cell("; ".join(row["detail"])),
                )
            )
        return block

    lines.append("## Look at these first")
    systematic = by_verdict[V_SYSTEMATIC]
    if systematic:
        for family in systematic:
            lines.extend(family_block(family))
    else:
        lines.append("No SYSTEMATIC_CANDIDATE rows; nothing demands a human first.")
    for verdict in VERDICT_ORDER:
        if verdict == V_SYSTEMATIC:
            continue
        families = by_verdict[verdict]
        lines.append(f"## {verdict} ({len(families)} family(ies), {verdict_counts[verdict]} rows)")
        for family in families:
            lines.extend(family_block(family))
    lines.append(LIMITS_PARAGRAPH)
    return "\n".join(lines) + "\n"
