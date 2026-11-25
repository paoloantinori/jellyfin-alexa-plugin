"""NLU results ledger (JF-954.5): the suite-side writer and the statistics.

Writer side: one JSON Lines file per live suite session under
tests/integration/results/ (gitignored), named
``nlu-<UTC yyyymmddThhmmssZ>-<pid>.jsonl``. Four record types, one JSON
object per line, each line flushed as written, so an interrupted run keeps
its rows: a file without a ``session_end`` record is an INCOMPLETE session
whose rows still count in the statistics. A session that recorded no rows at
all is deleted at close, because an empty file would only pollute the
statistics with a zero-row incomplete entry.

The writer never changes a test outcome. Every entry point catches its own
exceptions, reports the first fault of the session once as a warning, and
either continues with nulls (clock, git, model sha or live identity
unobtainable) or disables the ledger for the rest of the session (a file
write failure). The clock, git reader, identity reader, model sha reader and
file opener are injectable callables so the unit tests need no git, no clock
and no network. The live identity reader in the suite is the read-only
get-skill-status call behind ``SmapiClient.live_model_identity``.

Statistics side: sessions are ordered by ``started`` (ties by session_id);
the row key is ``(locale, normalize_text(utterance))``. Flip counting walks
SESSION adjacency: each session contributes one representative, the last
non-error row it recorded for the key (a session whose rows are all errors
represents nothing). ``flips_total`` counts adjacent representatives whose
``actual_intent`` differs; ``flips_same_model`` counts the same pairs
restricted to those where the committed ``model_sha256`` is equal AND the
locale's live identities are present, equal and stable within both
sessions AND the catalog canary state (JF-971) is present, ok and equal in
both sessions, so a flip counted there happened with no template, live-model
or CATALOG change. Flip pairs that are same-model by sha and identity but
whose catalog state is not proven-equal are counted separately as
``flips_catalog_excluded``, so the 2026-11-25 catalog-deletion class (equal
sha and eTag, dead catalogs) is visible as an exclusion count instead of
polluting ``flips_same_model``. ``last_outcome`` and ``last_actual_intent``
come from the last non-error row (errors are environmental and never read
as a verdict). Output is deterministic for a given set of ledger files:
sorted keys, sorted rows, no timestamps beyond the ones the files
themselves carry.
"""

from __future__ import annotations

import hashlib
import json
import os
import subprocess
import warnings
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable

from . import catalog_canary
from .effective_model import DEFAULT_MODELS_DIR
from .normalize import normalize_text

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_RESULTS_DIR = REPO_ROOT / "tests" / "integration" / "results"

LEDGER_SCHEMA = 1

# The one disabled-suite warning wording, shared by the writer's own fault
# handler and the conftest glue fault handler so the two never drift.
DISABLED_WARNING_TEMPLATE = "NLU ledger disabled for the rest of this session: {exc!r}"

# Outcome and failure-kind vocabularies of the ``row`` record.
OUTCOME_PASS = "pass"
OUTCOME_FAIL = "fail"
OUTCOME_ERROR = "error"


class NluLedgerWarning(UserWarning):
    """A ledger fault the suite must surface but must not fail a test on."""


# ---------------------------------------------------------------------------
# Failure-kind mapping (Part B wiring; AC 2)
# ---------------------------------------------------------------------------

# Each entry pairs a stable fragment of the assertion or pytest.fail message
# in tests/integration/test_nlu.py with the (outcome, failure_kind) the row
# carries. The assertions themselves must not change (the parent task rule),
# so these fragments are the contract between the suite and the ledger. The
# SMAPI error path and the SIGALRM timeout are environmental, so they map to
# outcome ``error``: flip statistics ignore error rows.
_FAILURE_CLASSIFIERS: tuple[tuple[str, tuple[str, str | None]], ...] = (
    ("Intent mismatch for", (OUTCOME_FAIL, "intent")),
    ("Missing slot '", (OUTCOME_FAIL, "slot_missing")),
    ("resolved empty for", (OUTCOME_FAIL, "slot_empty")),
    ("value drift for", (OUTCOME_FAIL, "slot_value")),
    ("SMAPI error for", (OUTCOME_ERROR, "smapi_error")),
    ("Test timed out after", (OUTCOME_ERROR, "smapi_error")),
)


def classify_failure(message: str) -> tuple[str, str | None]:
    """Map a pytest failure message to its (outcome, failure_kind) row fields.

    The first matching fragment wins, so an utterance that happens to embed a
    later fragment cannot out-prioritize the intent assertion that actually
    fired. A message matching no fragment is a genuine unknown shape: it keeps
    outcome ``fail`` with a null failure_kind and its detail line carries the
    message.
    """
    text = message or ""
    for fragment, mapped in _FAILURE_CLASSIFIERS:
        if fragment in text:
            return mapped
    return (OUTCOME_FAIL, None)


# ---------------------------------------------------------------------------
# Default readers
# ---------------------------------------------------------------------------


def _default_opener(path: Path):
    """Open the session file for writing (injectable for the failure tests)."""
    return path.open("w", encoding="utf-8")


def _default_git_reader() -> tuple[str | None, bool]:
    """(HEAD sha, dirty flag) from the repository; (None, False) on any failure."""
    try:
        head = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            capture_output=True,
            text=True,
            timeout=10,
            cwd=str(REPO_ROOT),
            check=False,
        ).stdout.strip()
        status = subprocess.run(
            ["git", "status", "--porcelain"],
            capture_output=True,
            text=True,
            timeout=10,
            cwd=str(REPO_ROOT),
            check=False,
        ).stdout
    except (OSError, subprocess.SubprocessError):
        return None, False
    return (head or None), bool(status.strip())


def _default_model_sha256(locale: str) -> str | None:
    """sha256 of the bytes of the committed model_<locale>.json; None if unreadable."""
    path = DEFAULT_MODELS_DIR / f"model_{locale}.json"
    try:
        return hashlib.sha256(path.read_bytes()).hexdigest()
    except OSError:
        return None


# ---------------------------------------------------------------------------
# The writer
# ---------------------------------------------------------------------------


def _stamp(moment: datetime) -> tuple[str, str]:
    """(compact filename stamp, ISO 8601 string) for one UTC moment."""
    return (
        moment.strftime("%Y%m%dT%H%M%SZ"),
        moment.strftime("%Y-%m-%dT%H:%M:%SZ"),
    )


class LedgerWriter:
    """Write one ledger session file. All faults are swallowed and reported."""

    def __init__(
        self,
        out_dir,
        *,
        clock: Callable[[], datetime] | None = None,
        git_reader: Callable[[], tuple[str | None, bool]] | None = None,
        identity_reader: Callable[[str], str | None] | None = None,
        model_sha256_fn: Callable[[str], str | None] | None = None,
        pid: int | None = None,
        opener: Callable[[Path], object] | None = None,
        catalog_reader: Callable[[str], dict | None] | None = None,
    ):
        self.out_dir = Path(out_dir)
        self._clock = clock or (lambda: datetime.now(timezone.utc))
        self._git_reader = git_reader or _default_git_reader
        # None means the suite runs without an identity source: identities
        # stay null and no SMAPI call is ever made.
        self._identity_reader = identity_reader
        # JF-971: None means no canary source is wired; the records then
        # carry the EXPLICIT unwired-reason block, never a silent absence.
        self._catalog_reader = catalog_reader
        self._model_sha256_fn = model_sha256_fn or _default_model_sha256
        self._pid = os.getpid() if pid is None else pid
        self._opener = opener or _default_opener
        self._warned = False
        self._disabled = False
        self._started = False
        self._finished = False
        self._handle = None
        self._row_count = 0
        self._locales: list[str] = []
        self.session_id: str | None = None
        self.path: Path | None = None
        self.errors: list[Exception] = []

    # -- state for tests and callers -------------------------------------

    @property
    def row_count(self) -> int:
        return self._row_count

    @property
    def complete(self) -> bool:
        return self._finished

    # -- fault handling ---------------------------------------------------

    def _fault(self, exc: Exception, fatal: bool) -> None:
        self.errors.append(exc)
        if not self._warned:
            self._warned = True
            if fatal:
                warnings.warn(
                    DISABLED_WARNING_TEMPLATE.format(exc=exc), NluLedgerWarning
                )
            else:
                warnings.warn(
                    f"NLU ledger continuing with nulls after a fault: {exc!r}",
                    NluLedgerWarning,
                )
        if fatal:
            self._disabled = True
            self._close_handle_quietly()

    def _try(self, fatal: bool, fn: Callable, default):
        """Run fn, swallowing and reporting any exception; default on failure."""
        if self._disabled:
            return default
        try:
            return fn()
        except Exception as exc:  # the ledger must never change a test outcome
            self._fault(exc, fatal)
            return default

    def _close_handle_quietly(self) -> None:
        if self._handle is not None:
            try:
                self._handle.close()
            except Exception:
                pass
            self._handle = None

    def _emit(self, record: dict) -> None:
        line = json.dumps(record, ensure_ascii=False, sort_keys=True)
        self._handle.write(line + "\n")
        self._handle.flush()

    def _try_identity(self, locale: str) -> str | None:
        if self._identity_reader is None:
            return None
        return self._try(False, lambda: self._identity_reader(locale), None)

    def _try_catalog(self, locale: str) -> dict:
        """The catalog state block for one locale; NEVER a silent absence.

        An unwired reader and a reader fault both become explicit not_run
        blocks (JF-971 AC 4): a silent skip would read as a passed canary
        in the history and a stats pair would silently count.
        """
        if self._catalog_reader is None:
            return catalog_canary.not_run_block(catalog_canary.REASON_UNWIRED_READER)
        block = self._try(False, lambda: self._catalog_reader(locale), None)
        if isinstance(block, dict):
            return block
        return catalog_canary.not_run_block(catalog_canary.REASON_READER_FAILED)

    # -- session lifecycle -------------------------------------------------

    def start_session(self, skill_id: str | None = None) -> None:
        """Open the session file and write the session_start record."""
        if self._disabled or self._started:
            return
        head, dirty = self._try(False, self._git_reader, (None, False))

        def action() -> None:
            compact, iso = _stamp(self._clock())
            self.out_dir.mkdir(parents=True, exist_ok=True)
            self.session_id = f"nlu-{compact}-{self._pid}"
            self.path = self.out_dir / f"{self.session_id}.jsonl"
            self._handle = self._opener(self.path)
            self._emit({
                "type": "session_start",
                "schema": LEDGER_SCHEMA,
                "session_id": self.session_id,
                "started": iso,
                "skill_id": skill_id,
                "git_head": head,
                "git_dirty": bool(dirty),
                # The per-locale detail is filled lazily by the locale_start
                # records that follow, one per exercised locale.
                "locales": {},
            })
            self._started = True

        self._try(True, action, None)

    def start_locale(self, locale: str) -> None:
        """Write the locale_start record the first time a locale is exercised."""
        if not self._started or self._disabled or self._finished:
            return
        if locale in self._locales:
            return
        sha = self._try(False, lambda: self._model_sha256_fn(locale), None)
        identity = self._try_identity(locale)
        catalog = self._try_catalog(locale)

        def action() -> None:
            self._emit({
                "type": "locale_start",
                "locale": locale,
                "model_sha256": sha,
                "live_identity_start": identity,
                "catalog_state_start": catalog,
            })
            self._locales.append(locale)

        self._try(True, action, None)

    def record_row(
        self,
        *,
        locale: str,
        utterance: str,
        expected_intent: str,
        expected_slots=None,
        actual_intent: str | None = None,
        slots=None,
        considered=None,
        outcome: str,
        failure_kind: str | None = None,
        detail: str | None = None,
    ) -> None:
        """Write one row record; lazily writes the locale_start record first.

        ``expected_slots`` is stored exactly as given (the fixture's verbatim
        object; an empty object for any-value expectations and for fixtures
        with no slot expectations), so later analysis never has to re-read a
        fixture file that may have changed since the run. ``considered``
        (JF-968) is the full consideredIntents hypotheses list ({intent,
        slots, er} dicts from the ONE extractor), annotation-only: it never
        gates an outcome, and rows written before the field default to []
        on read.
        """
        if self._disabled or not self._started or self._finished:
            return
        self.start_locale(locale)
        if self._disabled:
            return

        def action() -> None:
            self._emit({
                "type": "row",
                "locale": locale,
                "utterance": utterance,
                "expected_intent": expected_intent,
                "expected_slots": {} if expected_slots is None else expected_slots,
                "actual_intent": actual_intent,
                "slots": {} if slots is None else slots,
                "considered": [] if considered is None else considered,
                "outcome": outcome,
                "failure_kind": failure_kind,
                "detail": detail,
            })
            self._row_count += 1

        self._try(True, action, None)

    def record_row_filter(self, *, path: str, kept: int, total: int) -> None:
        """Write one row_filter record for a NLU_ROW_FILTER session (JF-954.7).

        ``path`` is the select output the suite was filtered by, ``total``
        the NLU rows the filter saw and ``kept`` those it kept, so a
        filtered session is distinguishable from a full-coverage one in the
        ledger history. Non-fatal by contract: a fault here warns once and
        the session keeps recording rows; it must never disable the writer
        or change a test outcome.
        """
        if self._disabled or not self._started or self._finished:
            return

        def action() -> None:
            self._emit({
                "type": "row_filter",
                "path": path,
                "kept": kept,
                "total": total,
            })

        self._try(False, action, None)

    def finish(self) -> None:
        """Write session_end and close; a session with no rows is removed.

        The zero-row orphan cleanup runs on EVERY finish path, including the
        disabled one: a fatal write failure before the first row would
        otherwise leave a session_start-only file that reads as a phantom
        incomplete session forever. A disabled session WITH rows keeps its
        file as an honest incomplete session (its rows still count).
        """
        if not self._started or self._finished:
            return
        identity_end: dict[str, str | None] = {}
        catalog_end: dict[str, dict] = {}
        if not self._disabled:
            for locale in sorted(self._locales):
                identity_end[locale] = self._try_identity(locale)
                catalog_end[locale] = self._try_catalog(locale)

        def close_out() -> None:
            # The orphan cleanup and the disabled keep-rows close must run on
            # every path, so they bypass the warning guard (the writer may
            # already be disabled, and _try would early-return on it).
            self._close_handle_quietly()
            if self._row_count == 0 and self.path is not None:
                self.path.unlink(missing_ok=True)
            self._finished = True

        if self._disabled or self._row_count == 0:
            try:
                close_out()
            except Exception as exc:  # quiet: finish is best effort by contract
                self.errors.append(exc)
            return

        def write_end() -> None:
            _, iso = _stamp(self._clock())
            self._emit({
                "type": "session_end",
                "ended": iso,
                "rows": self._row_count,
                "live_identity_end": identity_end,
                "catalog_state_end": catalog_end,
            })
            self._finished = True
            self._close_handle_quietly()

        self._try(True, write_end, None)

    def close(self) -> None:
        """Idempotent close of the file handle without writing session_end."""
        self._close_handle_quietly()


# ---------------------------------------------------------------------------
# Environment-based construction (the wiring seam; AC 4)
# ---------------------------------------------------------------------------


def writer_for_environment(
    dry_run: bool = False,
    out_dir: str | None = None,
    **writer_kwargs,
) -> LedgerWriter | None:
    """The writer the suite should use under the current environment, or None.

    Dry-run writes nothing; ``NLU_LEDGER=0`` disables the ledger and any other
    value or unset leaves it enabled; ``NLU_LEDGER_DIR`` overrides the output
    directory. Construction failures warn once and disable the ledger.
    """
    if dry_run:
        return None
    if os.environ.get("NLU_LEDGER", "") == "0":
        return None
    directory = out_dir or os.environ.get("NLU_LEDGER_DIR") or str(DEFAULT_RESULTS_DIR)
    try:
        return LedgerWriter(out_dir=directory, **writer_kwargs)
    except Exception as exc:  # construction must never break the suite
        warnings.warn(f"NLU ledger disabled: {exc!r}", NluLedgerWarning)
        return None


# ---------------------------------------------------------------------------
# Reading sessions back (Part C)
# ---------------------------------------------------------------------------


def load_sessions(results_dir) -> list[dict]:
    """Parse every nlu-*.jsonl file in results_dir into normalized session dicts.

    Only the writer's own ``nlu-<stamp>-<pid>.jsonl`` naming is read, so
    unrelated JSONL files sharing the directory never become phantom
    sessions. A torn tail line (an interrupted run killed mid-write) is
    skipped. A file with no session_start still yields a session (started
    empty, incomplete), so no recorded row is ever dropped.
    """
    results_dir = Path(results_dir)
    sessions: list[dict] = []
    for path in sorted(results_dir.glob("nlu-*.jsonl")):
        session: dict = {
            "session_id": path.stem,
            "started": "",
            "skill_id": None,
            "git_head": None,
            "git_dirty": False,
            "complete": False,
            "rows": [],
            "locales": {},
        }
        text = path.read_text(encoding="utf-8", errors="replace")
        for line in text.splitlines():
            line = line.strip()
            if not line:
                continue
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                continue
            if not isinstance(record, dict):
                continue
            rtype = record.get("type")
            if rtype == "session_start":
                session["started"] = str(record.get("started") or "")
                session["skill_id"] = record.get("skill_id")
                session["git_head"] = record.get("git_head")
                session["git_dirty"] = bool(record.get("git_dirty"))
            elif rtype == "locale_start":
                loc = str(record.get("locale") or "")
                if not loc:
                    continue
                entry = _locale_entry(session, loc)
                entry["model_sha256"] = record.get("model_sha256")
                entry["live_identity_start"] = record.get("live_identity_start")
                # JF-971: pre-canary files carry no block; the default None
                # is the documented UNKNOWN the stats exclude on.
                entry["catalog_state_start"] = record.get("catalog_state_start")
            elif rtype == "row":
                # JF-968: pre-field files carry no considered key; the read
                # side owns the documented default so consumers may index it.
                record.setdefault("considered", [])
                session["rows"].append(record)
            elif rtype == "row_filter":
                # JF-954.7: the session was run under NLU_ROW_FILTER; keep
                # the trace so a filtered session is distinguishable from a
                # full-coverage one. Unknown record types stay skipped, so
                # files carrying this record still load on an older reader.
                session["row_filter"] = {
                    "path": record.get("path"),
                    "kept": record.get("kept"),
                    "total": record.get("total"),
                }
            elif rtype == "session_end":
                session["complete"] = True
                ends = record.get("live_identity_end")
                if isinstance(ends, dict):
                    for loc, identity in ends.items():
                        entry = _locale_entry(session, str(loc))
                        entry["live_identity_end"] = identity
                blocks = record.get("catalog_state_end")
                if isinstance(blocks, dict):
                    for loc, block in blocks.items():
                        entry = _locale_entry(session, str(loc))
                        entry["catalog_state_end"] = block
        sessions.append(session)
    sessions.sort(key=lambda s: (s["started"], s["session_id"]))
    return sessions


def _locale_entry(session: dict, locale: str) -> dict:
    return session["locales"].setdefault(
        locale,
        {
            "model_sha256": None,
            "live_identity_start": None,
            "live_identity_end": None,
            "catalog_state_start": None,
            "catalog_state_end": None,
        },
    )


def rebuilt_during_run(locale_entry: dict) -> bool | None:
    """True when both live identities exist and differ; null when either is null.

    True means a rebuild happened DURING the session, so the identities the
    session's rows were served under are not one model and flip reasoning
    across that session is suspect.
    """
    start = locale_entry.get("live_identity_start")
    end = locale_entry.get("live_identity_end")
    if start is None or end is None:
        return None
    return start != end


def _locale_sha(session: dict, locale: str) -> str | None:
    entry = session["locales"].get(locale)
    if not entry:
        return None
    return entry.get("model_sha256")


# ---------------------------------------------------------------------------
# Statistics
# ---------------------------------------------------------------------------


def compute_stats(
    sessions: list[dict],
    locale: str | None = None,
    last: int | None = None,
) -> dict:
    """The deterministic statistics payload over the given sessions.

    ``locale`` restricts the row keys (and each session's locale map) to one
    locale; with a locale given, the sessions are first restricted to those
    that exercised it, so ``--last N`` means the newest N sessions OF THAT
    LOCALE. ``last`` alone restricts to the newest N sessions by the session
    order (started, then session_id).
    """
    if locale is not None:
        sessions = [s for s in sessions if locale in s["locales"]]
    if last is not None:
        if int(last) <= 0:
            raise ValueError("--last must be a positive integer")
        sessions = sessions[-int(last):]
    grouped: dict[tuple[str, str], list[tuple[dict, dict]]] = {}
    for session in sessions:
        for row in session["rows"]:
            row_locale = str(row.get("locale") or "")
            if locale is not None and row_locale != locale:
                continue
            utterance = normalize_text(str(row.get("utterance") or ""))
            grouped.setdefault((row_locale, utterance), []).append((session, row))
    row_stats = [_row_stats(key, entries) for key, entries in sorted(grouped.items())]
    session_payloads = [session_payload(s, locale) for s in sessions]
    return {"schema": LEDGER_SCHEMA, "sessions": session_payloads, "rows": row_stats}


def _row_stats(key: tuple[str, str], entries: list[tuple[dict, dict]]) -> dict:
    loc, utterance = key
    passes = fails = errors = 0
    for _, row in entries:
        outcome = row.get("outcome")
        if outcome == OUTCOME_PASS:
            passes += 1
        elif outcome == OUTCOME_FAIL:
            fails += 1
        elif outcome == OUTCOME_ERROR:
            errors += 1
    actual_intents = sorted(
        {str(r.get("actual_intent")) for _, r in entries if r.get("actual_intent")}
    )
    # Flip counting walks SESSION adjacency, not row adjacency: each session
    # contributes ONE representative, the last non-error row it recorded for
    # the key (so a duplicated utterance inside one session never flips
    # against itself), and a session whose rows are all errors represents
    # nothing. Runs, passes, fails and errors still count ROWS.
    representatives: list[tuple[dict, dict]] = []
    for session, row in entries:
        if row.get("outcome") == OUTCOME_ERROR:
            continue
        if representatives and representatives[-1][0] is session:
            representatives[-1] = (session, row)  # the session's LAST non-error row
        else:
            representatives.append((session, row))
    flips_total = 0
    flips_same_model = 0
    flips_catalog_excluded = 0
    for index in range(1, len(representatives)):
        s_prev, r_prev = representatives[index - 1]
        s_next, r_next = representatives[index]
        if r_prev.get("actual_intent") == r_next.get("actual_intent"):
            continue
        flips_total += 1
        # One proven-sha-and-identity evaluation, then the catalog test
        # decides which counter (if any) the flip lands in: same-model,
        # catalog-only exclusion (the 2026-11-25 class), or neither.
        if _pair_proven_sha_identity(s_prev, s_next, loc):
            if _pair_catalog_equal(s_prev, s_next, loc):
                flips_same_model += 1
            else:
                flips_catalog_excluded += 1
    # last_outcome and last_actual_intent come from the last NON-error row:
    # a session-ending SMAPI outage is environmental noise, not the row's
    # routing verdict; null when every recorded row for the key is an error.
    last_outcome = None
    last_actual_intent = None
    for _session, row in reversed(entries):
        if row.get("outcome") != OUTCOME_ERROR:
            last_outcome = row.get("outcome")
            last_actual_intent = row.get("actual_intent")
            break
    return {
        "locale": loc,
        "utterance": utterance,
        "runs": len(entries),
        "passes": passes,
        "fails": fails,
        "errors": errors,
        "actual_intents": actual_intents,
        "flips_total": flips_total,
        "flips_same_model": flips_same_model,
        "flips_catalog_excluded": flips_catalog_excluded,
        "last_outcome": last_outcome,
        "last_actual_intent": last_actual_intent,
        "first_session": entries[0][0]["session_id"],
        "last_session": entries[-1][0]["session_id"],
    }


def _pair_proven_sha_identity(session_prev: dict, session_next: dict, locale: str) -> bool:
    """True when the flip pair provably ran the same committed bytes under
    the same live model: the sha and identity gate BOTH flip counters share.

    The committed model bytes (model_sha256) and the LIVE model must be
    equal: the 2026-10-19 catalog-displacement class changed the live model
    without touching the committed json. Each session's live identity counts
    only when present AND stable (start equals end); anything unproven is
    False and the flip stays in flips_total alone.
    """
    sha_prev = _locale_sha(session_prev, locale)
    sha_next = _locale_sha(session_next, locale)
    if sha_prev is None or sha_prev != sha_next:
        return False
    identity_prev = _stable_identity(session_prev, locale)
    identity_next = _stable_identity(session_next, locale)
    return (
        identity_prev is not None
        and identity_next is not None
        and identity_prev == identity_next
    )


def _pair_catalog_equal(session_prev: dict, session_next: dict, locale: str) -> bool:
    """True when both sessions carry a proven, clean, equal catalog state.

    The catalog leg of the same-model decision in _row_stats; each side's
    state is None unless it is present at both endcaps, ok, equal within
    the session and free of failed and unknown probes
    (catalog_canary.entry_stable_canonical, the Gate 2 contract).
    """
    catalog_prev = _stable_catalog(session_prev, locale)
    catalog_next = _stable_catalog(session_next, locale)
    return (
        catalog_prev is not None
        and catalog_next is not None
        and catalog_prev == catalog_next
    )


def _stable_identity(session: dict, locale: str) -> str | None:
    """The session's one live identity for the locale, or None.

    None when the locale entry is missing, either identity is null, or a
    rebuild happened during the session (identities differ).
    """
    entry = session["locales"].get(locale)
    if not entry:
        return None
    start = entry.get("live_identity_start")
    end = entry.get("live_identity_end")
    if start is None or end is None or start != end:
        return None
    return start


def _stable_catalog(session: dict, locale: str) -> str | None:
    """The session's ONE catalog canary state for the locale, or None.

    Delegates to catalog_canary.entry_stable_canonical (the ONE state
    comparison): None when either block is missing, not_run, or the state
    changed during the session, exactly like _stable_identity treats a
    mid-session rebuild.
    """
    entry = session["locales"].get(locale)
    if not entry:
        return None
    return catalog_canary.entry_stable_canonical(entry)


def _block_status(block) -> str | None:
    """A block's status string, or None for anything that is not a block."""
    return block.get("status") if isinstance(block, dict) else None


def session_payload(session: dict, locale_filter: str | None = None) -> dict:
    locales: dict[str, dict] = {}
    for loc, entry in sorted(session["locales"].items()):
        if locale_filter is not None and loc != locale_filter:
            continue
        start_status = _block_status(entry.get("catalog_state_start"))
        end_status = _block_status(entry.get("catalog_state_end"))
        enriched = {
            "model_sha256": entry.get("model_sha256"),
            "live_identity_start": entry.get("live_identity_start"),
            "live_identity_end": entry.get("live_identity_end"),
            "rebuilt_during_run": rebuilt_during_run(entry),
            # JF-971: the catalog canary summary. stable is the ONE
            # comparable-state test the flip pair rule uses; None statuses
            # mean the session carried no block (pre-JF-971 file or a locale
            # the canary never covered).
            "catalog": {
                "start": start_status,
                "end": end_status,
                "stable": _stable_catalog(session, loc) is not None,
            },
        }
        locales[loc] = enriched
    return {
        "session_id": session["session_id"],
        "started": session["started"],
        "skill_id": session["skill_id"],
        "git_head": session["git_head"],
        "git_dirty": session["git_dirty"],
        "complete": session["complete"],
        "rows": len(session["rows"]),
        "row_filter": session.get("row_filter"),
        "locales": locales,
    }


# ---------------------------------------------------------------------------
# Deterministic text rendering
# ---------------------------------------------------------------------------


def _flag(value) -> str:
    return {True: "true", False: "false", None: "null"}.get(value, str(value))


def _session_line(session: dict, prefix: str = "") -> str:
    """The one session summary line shared by ``list`` and the stats text."""
    return (
        f"{prefix}{session['session_id']} started={session['started']} "
        f"complete={'yes' if session['complete'] else 'no'} "
        f"rows={session['rows']}"
    )


def render_sessions_text(session_payloads: list[dict]) -> str:
    """The ``ledger list`` text: one line per session with completeness and rebuilt flags."""
    lines = [f"nlu ledger sessions: {len(session_payloads)}"]
    for session in session_payloads:
        rebuilt = ", ".join(
            f"{loc}={_flag(entry['rebuilt_during_run'])}"
            for loc, entry in sorted(session["locales"].items())
        )
        suffix = f" rebuilt: {rebuilt}" if rebuilt else ""
        lines.append(_session_line(session) + suffix)
    return "\n".join(lines) + "\n"


def render_stats_text(stats: dict) -> str:
    """The ``ledger stats`` text form: sessions first, then one block per row key."""
    sessions = stats["sessions"]
    incomplete = sum(1 for s in sessions if not s["complete"])
    lines = [
        "nlu ledger stats",
        f"sessions: {len(sessions)} ({incomplete} incomplete)",
    ]
    for session in sessions:
        lines.append(_session_line(session, prefix="session "))
        for loc, entry in sorted(session["locales"].items()):
            catalog = entry["catalog"]
            lines.append(
                f"  {loc}: model={entry['model_sha256'] or '-'} "
                f"rebuilt_during_run={_flag(entry['rebuilt_during_run'])} "
                f"catalog={catalog['start'] or '-'}/{catalog['end'] or '-'} "
                f"catalog_stable={_flag(catalog['stable'])}"
            )
    lines.append(f"row keys: {len(stats['rows'])}")
    for row in stats["rows"]:
        lines.append(f"[{row['locale']}] {row['utterance']}")
        lines.append(
            f"  runs={row['runs']} passes={row['passes']} fails={row['fails']} "
            f"errors={row['errors']} flips_total={row['flips_total']} "
            f"flips_same_model={row['flips_same_model']} "
            f"flips_catalog_excluded={row['flips_catalog_excluded']}"
        )
        intents = ", ".join(row["actual_intents"]) if row["actual_intents"] else "-"
        lines.append(f"  actual_intents: {intents}")
        lines.append(
            f"  last_outcome={row['last_outcome']} "
            f"last_actual_intent={row['last_actual_intent'] or '-'} "
            f"first_session={row['first_session']} last_session={row['last_session']}"
        )
    return "\n".join(lines) + "\n"
