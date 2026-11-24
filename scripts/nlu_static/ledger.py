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
the row key is ``(locale, normalize_text(utterance))``. ``flips_total``
counts adjacent non-error rows of a key whose ``actual_intent`` differs;
``flips_same_model`` counts the same pairs restricted to sessions whose
``model_sha256`` for that locale is equal, so a flip counted there happened
with no template change. Output is deterministic for a given set of ledger
files: sorted keys, sorted rows, no timestamps beyond the ones the files
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
    ):
        self.out_dir = Path(out_dir)
        self._clock = clock or (lambda: datetime.now(timezone.utc))
        self._git_reader = git_reader or _default_git_reader
        # None means the suite runs without an identity source: identities
        # stay null and no SMAPI call is ever made.
        self._identity_reader = identity_reader
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

        def action() -> None:
            self._emit({
                "type": "locale_start",
                "locale": locale,
                "model_sha256": sha,
                "live_identity_start": identity,
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
        outcome: str,
        failure_kind: str | None = None,
        detail: str | None = None,
    ) -> None:
        """Write one row record; lazily writes the locale_start record first.

        ``expected_slots`` is stored exactly as given (the fixture's verbatim
        object; an empty object for any-value expectations and for fixtures
        with no slot expectations), so later analysis never has to re-read a
        fixture file that may have changed since the run.
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
                "outcome": outcome,
                "failure_kind": failure_kind,
                "detail": detail,
            })
            self._row_count += 1

        self._try(True, action, None)

    def finish(self) -> None:
        """Write session_end and close; a session with no rows is removed."""
        if self._disabled or not self._started or self._finished:
            return
        identity_end: dict[str, str | None] = {}
        for locale in sorted(self._locales):
            identity_end[locale] = self._try_identity(locale)

        def action() -> None:
            if self._row_count == 0:
                # Nothing was exercised (a -k filter, an E2E-only run): the
                # file holds only session_start and would read as a spurious
                # incomplete session, so it is removed.
                self._close_handle_quietly()
                if self.path is not None:
                    self.path.unlink(missing_ok=True)
                self._finished = True
                return
            _, iso = _stamp(self._clock())
            self._emit({
                "type": "session_end",
                "ended": iso,
                "rows": self._row_count,
                "live_identity_end": identity_end,
            })
            self._finished = True
            self._close_handle_quietly()

        self._try(True, action, None)

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
            elif rtype == "row":
                session["rows"].append(record)
            elif rtype == "session_end":
                session["complete"] = True
                ends = record.get("live_identity_end")
                if isinstance(ends, dict):
                    for loc, identity in ends.items():
                        entry = _locale_entry(session, str(loc))
                        entry["live_identity_end"] = identity
        sessions.append(session)
    sessions.sort(key=lambda s: (s["started"], s["session_id"]))
    return sessions


def _locale_entry(session: dict, locale: str) -> dict:
    return session["locales"].setdefault(
        locale,
        {"model_sha256": None, "live_identity_start": None, "live_identity_end": None},
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
    # Error rows are environmental and are dropped before the adjacency walk,
    # so an outage between two passes is never a flip.
    non_error = [(s, r) for s, r in entries if r.get("outcome") != OUTCOME_ERROR]
    flips_total = 0
    flips_same_model = 0
    for index in range(1, len(non_error)):
        s_prev, r_prev = non_error[index - 1]
        s_next, r_next = non_error[index]
        if r_prev.get("actual_intent") == r_next.get("actual_intent"):
            continue
        flips_total += 1
        sha_prev = _locale_sha(s_prev, loc)
        sha_next = _locale_sha(s_next, loc)
        if sha_prev is not None and sha_prev == sha_next:
            flips_same_model += 1
    _, r_last = entries[-1]
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
        "last_outcome": r_last.get("outcome"),
        "last_actual_intent": r_last.get("actual_intent"),
        "first_session": entries[0][0]["session_id"],
        "last_session": entries[-1][0]["session_id"],
    }


def session_payload(session: dict, locale_filter: str | None = None) -> dict:
    locales: dict[str, dict] = {}
    for loc, entry in sorted(session["locales"].items()):
        if locale_filter is not None and loc != locale_filter:
            continue
        enriched = {
            "model_sha256": entry.get("model_sha256"),
            "live_identity_start": entry.get("live_identity_start"),
            "live_identity_end": entry.get("live_identity_end"),
            "rebuilt_during_run": rebuilt_during_run(entry),
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
            lines.append(
                f"  {loc}: model={entry['model_sha256'] or '-'} "
                f"rebuilt_during_run={_flag(entry['rebuilt_during_run'])}"
            )
    lines.append(f"row keys: {len(stats['rows'])}")
    for row in stats["rows"]:
        lines.append(f"[{row['locale']}] {row['utterance']}")
        lines.append(
            f"  runs={row['runs']} passes={row['passes']} fails={row['fails']} "
            f"errors={row['errors']} flips_total={row['flips_total']} "
            f"flips_same_model={row['flips_same_model']}"
        )
        intents = ", ".join(row["actual_intents"]) if row["actual_intents"] else "-"
        lines.append(f"  actual_intents: {intents}")
        lines.append(
            f"  last_outcome={row['last_outcome']} "
            f"last_actual_intent={row['last_actual_intent'] or '-'} "
            f"first_session={row['first_session']} last_session={row['last_session']}"
        )
    return "\n".join(lines) + "\n"
