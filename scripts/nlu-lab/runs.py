"""Run schema: one battery run's snapshot (RunRecord + RowResult) and the change-driven
diff/judgment table between two snapshots (RunRecord.diff_against).

Judgment rules (the change-driven matrix): a diff is keyed by (utterance, source) and judged
ONLY against each run's own snapshot expectations; a row that violates its expectation in
both runs is still "same". Standing failures surface via expectation_failure_count instead.
Expectation satisfaction splits expected_intent on '|' and treats MEMBERSHIP as satisfied
(the fixtures carry pipe alternatives in every locale).
"""
from dataclasses import dataclass, field
from enum import Enum
import json
import os
from pathlib import Path

DIFF_KINDS = ("same", "green", "red", "amber", "error", "absent_old", "absent_new")


class RunLoadError(RuntimeError):
    """A run file exists but cannot be parsed into a RunRecord. The corrupt-run
    guard lives in load_run itself (not in each caller) so every consumer - the
    CLI's compare, the webapp's run list - catches one typed exception instead
    of a raw json/KeyError/TypeError escaping as a traceback."""


class RowStatus(str, Enum):
    OK = "ok"
    NO_SELECTION = "no_selection"
    ERROR = "error"


@dataclass
class RowResult:
    utterance: str
    source: str
    status: RowStatus
    expected_intent: str | None
    selected_intent: str | None
    slots: dict
    considered_intents: list[str]
    raw: dict
    # JF-968: the full consideredIntents hypotheses (smapi_client.
    # considered_hypotheses shape: intent name, slot values, ER summaries).
    # Defaults to [] so run files written before the field keep loading.
    considered_hypotheses: list[dict] = field(default_factory=list)
    # JF-936: the failure text of an ERROR row (the ask CLI's stderr rides
    # the profile exception's message; a parser failure carries its
    # diagnostics). None on non-error rows and in run files written before
    # the field. The ERROR status is reused, not split: diff_against and
    # expectation_failure_count already exclude ERROR rows from judgment,
    # and a new status value would make new files unreadable by older
    # loaders (load_run's RowStatus(value) rejects unknown values).
    error: str | None = None

    @property
    def expectation_alternatives(self) -> list[str] | None:
        """expected_intent split on '|'; None when the row carries no usable expectation."""
        if self.expected_intent is None or not self.expected_intent.strip():
            return None
        alts = [p for p in (part.strip() for part in self.expected_intent.split("|")) if p]
        return alts or None

    def satisfies_expectation(self) -> bool:
        """Membership in the pipe alternatives; vacuously true with no expectation."""
        alts = self.expectation_alternatives
        return alts is None or self.selected_intent in alts


@dataclass
class RowDiff:
    utterance: str
    source: str
    kind: str  # one of DIFF_KINDS


@dataclass
class RunRecord:
    id: str
    timestamp: str
    locale: str
    label: str
    model_source: str
    model_sha256: str
    skill_id: str
    status: str  # ok | failed | interrupted
    error: str | None = None
    # Amazon's per-build identity (the JF-934 eTag): recorded on BOTH the
    # deploy path (post-wait) and the skip-deploy path (at start), so compare
    # can refuse an A/A pair whose two runs probed different BUILDS even when
    # the model file sha matches (the false-pass direction of the sha guard).
    build_etag: str | None = None
    # JF-971: the ER canary blocks (catalog_canary block dicts: status ok or
    # not_run with a mandatory reason). Recorded on the --live path at run
    # start and run end, so the catalog state the rows ran under is part of
    # the run's own identity like build_etag. None on non-live paths and in
    # files written before the field.
    catalog_start: dict | None = None
    catalog_end: dict | None = None
    intent_roster: list[dict] = field(default_factory=list)
    results: list[RowResult] = field(default_factory=list)

    @property
    def expectation_failure_count(self) -> int:
        """Non-ERROR rows with a non-null expectation they violate. NO_SELECTION with an
        expectation counts as a failure; only ERROR rows are excluded."""
        return sum(1 for r in self.results
                   if r.status is not RowStatus.ERROR
                   and r.expectation_alternatives is not None
                   and not r.satisfies_expectation())

    def diff_against(self, old: "RunRecord") -> list[RowDiff]:
        """Change-driven judgment table. Both sides' ERROR rows map to "error" regardless of
        change; a new-side ERROR maps to "error" even without an old counterpart, because an
        error row cannot be judged. Unchanged selections are "same" even when the expectation
        is violated in both snapshots (standing failures live in expectation_failure_count)."""
        old_rows = {(r.utterance, r.source): r for r in old.results}
        new_rows = {(r.utterance, r.source): r for r in self.results}
        diffs: list[RowDiff] = []
        for (utterance, source), new in new_rows.items():
            prev = old_rows.get((utterance, source))
            if prev is None:
                kind = "error" if new.status is RowStatus.ERROR else "absent_new"
            elif RowStatus.ERROR in (prev.status, new.status):
                kind = "error"
            elif prev.selected_intent == new.selected_intent:
                kind = "same"
            elif new.expectation_alternatives is None:
                kind = "amber"
            elif new.satisfies_expectation():
                kind = "green"
            else:
                kind = "red"
            diffs.append(RowDiff(utterance=utterance, source=source, kind=kind))
        for utterance, source in old_rows:
            if (utterance, source) not in new_rows:
                diffs.append(RowDiff(utterance=utterance, source=source, kind="absent_old"))
        return diffs


def _row_to_dict(r: RowResult) -> dict:
    return {
        "utterance": r.utterance,
        "source": r.source,
        "status": r.status.value,
        "expected_intent": r.expected_intent,
        "selected_intent": r.selected_intent,
        "slots": r.slots,
        "considered_intents": r.considered_intents,
        "raw": r.raw,
        "considered_hypotheses": r.considered_hypotheses,
        "error": r.error,
    }


def _to_dict(run: RunRecord) -> dict:
    return {
        "id": run.id,
        "timestamp": run.timestamp,
        "locale": run.locale,
        "label": run.label,
        "model_source": run.model_source,
        "model_sha256": run.model_sha256,
        "skill_id": run.skill_id,
        "status": run.status,
        "error": run.error,
        "build_etag": run.build_etag,
        "catalog_start": run.catalog_start,
        "catalog_end": run.catalog_end,
        "intent_roster": run.intent_roster,
        "results": [_row_to_dict(r) for r in run.results],
    }


def save_run(runs_dir: Path, run: RunRecord) -> Path:
    """Write {id}.json atomically (tmp file plus rename) and return the final path."""
    runs_dir = Path(runs_dir)
    runs_dir.mkdir(parents=True, exist_ok=True)
    path = runs_dir / f"{run.id}.json"
    tmp = path.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(_to_dict(run), indent=2))
    os.replace(tmp, path)
    return path


def load_run(path: Path) -> RunRecord:
    """Read a run file; unknown extra keys are ignored (schema tolerance for
    older files). Raises RunLoadError (never a raw json/KeyError/TypeError/
    OSError) when the file cannot be read or parsed."""
    try:
        data = json.loads(Path(path).read_text())
        results = [
            RowResult(
                utterance=r["utterance"],
                source=r["source"],
                status=RowStatus(r["status"]),
                expected_intent=r.get("expected_intent"),
                selected_intent=r.get("selected_intent"),
                slots=r.get("slots") or {},
                considered_intents=r.get("considered_intents") or [],
                raw=r.get("raw") or {},
                considered_hypotheses=r.get("considered_hypotheses") or [],
                error=r.get("error"),
            )
            for r in data.get("results", [])
        ]
        return RunRecord(
            id=data["id"],
            timestamp=data["timestamp"],
            locale=data["locale"],
            label=data["label"],
            model_source=data["model_source"],
            model_sha256=data["model_sha256"],
            skill_id=data["skill_id"],
            status=data["status"],
            error=data.get("error"),
            build_etag=data.get("build_etag"),
            catalog_start=data.get("catalog_start"),
            catalog_end=data.get("catalog_end"),
            intent_roster=data.get("intent_roster") or [],
            results=results,
        )
    except (ValueError, KeyError, TypeError, AttributeError, OSError) as exc:
        raise RunLoadError(f"run file {path} unreadable: {exc}") from exc
