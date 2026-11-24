"""JF-954.5: the NLU results ledger battery.

Writer tests build session files in tmp_path with injected clock, git,
identity and model-sha callables (and an injectable opener for the failure
case), so no test touches git, the clock, SMAPI, the ask CLI or Jellyfin.
Statistics tests build small ledger directories through the same writer and
assert the six guidance scenarios plus the acceptance criteria. The only
committed-tree reads are the .gitignore check and the doc check.
"""

import json
import sys
from datetime import datetime, timezone
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO / "scripts"
INTEGRATION_DIR = REPO / "tests" / "integration"
for entry in (str(SCRIPTS_DIR), str(INTEGRATION_DIR)):
    if entry not in sys.path:
        sys.path.insert(0, entry)

from nlu_static import cli as nlu_cli  # noqa: E402
from nlu_static import ledger as nlu_ledger  # noqa: E402
from nlu_static.ledger import NluLedgerWarning  # noqa: E402
import smapi_client as smapi_client_module  # noqa: E402
from smapi_client import SmapiClient, SmapiError  # noqa: E402

FIXED_MOMENT = datetime(2026, 11, 24, 12, 0, 0, tzinfo=timezone.utc)

# The alarm-timeout message the conftest wiring maps to smapi_error, built
# from the codepoint so the banned character never lands in this file. The
# real message is pinned by tests/integration/conftest.py, which the ledger
# classifies verbatim.
_ALARM_TIMEOUT_MESSAGE = (
    f"Test timed out after 120s {chr(0x2014)} SMAPI likely rate-limited or "
    "simulation stuck."
)


def _fixed_clock(minute: int = 0):
    return lambda: FIXED_MOMENT.replace(minute=minute)


def _writer(tmp_path: Path, **over) -> nlu_ledger.LedgerWriter:
    kwargs = dict(
        out_dir=tmp_path,
        clock=_fixed_clock(),
        git_reader=lambda: ("head123", False),
        identity_reader=lambda locale: "etag-1",
        model_sha256_fn=lambda locale: f"sha-{locale}",
        pid=4242,
    )
    kwargs.update(over)
    return nlu_ledger.LedgerWriter(**kwargs)


def _run_session(
    out_dir: Path,
    pid: int,
    rows,
    *,
    minute: int = 0,
    sha: str | None = "sha-A",
    identities="etag-A",
    complete: bool = True,
    skill_id="sk-1",
) -> None:
    """Write one session file; rows are (utterance, actual_intent, outcome) triples."""
    counter = {"n": 0}

    def identity_reader(_locale):
        if isinstance(identities, str):
            return identities
        value = identities[counter["n"]]
        counter["n"] += 1
        return value

    writer = _writer(
        out_dir,
        pid=pid,
        clock=_fixed_clock(minute),
        identity_reader=identity_reader,
        model_sha256_fn=lambda _locale: sha,
    )
    writer.start_session(skill_id)
    for utterance, actual_intent, outcome in rows:
        writer.record_row(
            locale="it-IT",
            utterance=utterance,
            expected_intent="LoopAllOffIntent",
            actual_intent=actual_intent,
            outcome=outcome,
        )
    if complete:
        writer.finish()


# --- AC 1: records, flush before close -------------------------------------------------


def test_session_writes_all_records_and_flushes_before_close(tmp_path):
    writer = _writer(tmp_path, pid=77)
    writer.start_session("sk-1")
    writer.record_row(
        locale="it-IT",
        utterance="disattiva loop",
        expected_intent="LoopAllOffIntent",
        expected_slots={},
        actual_intent="LoopAllOffIntent",
        slots={},
        outcome="pass",
    )
    (session_file,) = tmp_path.glob("*.jsonl")
    assert session_file.name == "nlu-20261124T120000Z-77.jsonl"
    # Read BEFORE close: every line is already on disk (flush per line).
    lines = session_file.read_text(encoding="utf-8").splitlines()
    records = [json.loads(line) for line in lines]
    assert [record["type"] for record in records] == [
        "session_start", "locale_start", "row",
    ]
    start, locale_start, row = records
    assert start["schema"] == 1
    assert start["session_id"] == session_file.stem
    assert start["skill_id"] == "sk-1"
    assert start["started"] == "2026-11-24T12:00:00Z"
    assert start["git_head"] == "head123"
    assert start["git_dirty"] is False
    assert start["locales"] == {}
    assert locale_start["locale"] == "it-IT"
    assert locale_start["model_sha256"] == "sha-it-IT"
    assert locale_start["live_identity_start"] == "etag-1"
    assert row["outcome"] == "pass"
    assert row["failure_kind"] is None
    assert row["actual_intent"] == "LoopAllOffIntent"
    writer.finish()
    lines = session_file.read_text(encoding="utf-8").splitlines()
    end = json.loads(lines[-1])
    assert end["type"] == "session_end"
    assert end["rows"] == 1
    assert "ended" in end
    assert end["live_identity_end"] == {"it-IT": "etag-1"}
    assert writer.complete is True


def test_session_with_multiple_locales_writes_one_locale_start_each(tmp_path):
    writer = _writer(tmp_path, pid=78)
    writer.start_session("sk-1")
    for locale in ("it-IT", "es-ES", "it-IT"):
        writer.record_row(
            locale=locale,
            utterance="x",
            expected_intent="A",
            actual_intent="A",
            outcome="pass",
        )
    writer.finish()
    (session_file,) = tmp_path.glob("*.jsonl")
    records = [json.loads(line) for line in session_file.read_text(encoding="utf-8").splitlines()]
    locale_starts = [r["locale"] for r in records if r["type"] == "locale_start"]
    assert locale_starts == ["it-IT", "es-ES"]


def test_session_with_zero_rows_is_removed_at_finish(tmp_path):
    writer = _writer(tmp_path, pid=79)
    writer.start_session("sk-1")
    writer.finish()
    assert list(tmp_path.glob("*.jsonl")) == []


# --- AC 2: the failure-kind mapping -----------------------------------------------------

_SUITE_FAILURE_MESSAGES = [
    pytest.param(
        "Intent mismatch for 'disattiva loop' (it-IT):\n"
        "  expected: LoopAllOffIntent\n  actual:   LoopAllOnIntent",
        ("fail", "intent"),
        id="intent",
    ),
    pytest.param(
        "Missing slot 'series_name' for 'pon x' (es-ES):\n"
        "  expected slots: ['series_name']\n  resolved slots: []",
        ("fail", "slot_missing"),
        id="slot_missing",
    ),
    pytest.param(
        "Slot 'series_name' resolved empty for 'pon x' (es-ES):\n"
        "  NLU matched intent but did not fill the slot",
        ("fail", "slot_empty"),
        id="slot_empty",
    ),
    pytest.param(
        "Slot 'series_name' value drift for 'pon x' (es-ES):\n"
        "  expected: 'adolescencia'\n  resolved: 'breaking bad'",
        ("fail", "slot_value"),
        id="slot_value",
    ),
    pytest.param(
        "Failed: SMAPI error for 'pon x' (es-ES): ASK CLI failed (exit 1): boom",
        ("error", "smapi_error"),
        id="smapi_error",
    ),
    pytest.param(
        _ALARM_TIMEOUT_MESSAGE,
        ("error", "smapi_error"),
        id="alarm_timeout",
    ),
    pytest.param("some entirely unexpected failure shape", ("fail", None), id="unknown"),
]


@pytest.mark.parametrize("message,want", _SUITE_FAILURE_MESSAGES)
def test_classify_failure_maps_suite_assertion_messages(message, want):
    assert nlu_ledger.classify_failure(message) == want


def test_classify_failure_intent_mismatch_wins_over_embedded_fragments():
    # The utterance itself embeds a later fragment; the intent assertion fired
    # and must win (first-match order in the classifier table).
    message = (
        "Intent mismatch for 'say value drift for me' (en-US):\n"
        "  expected: A\n  actual:   B"
    )
    assert nlu_ledger.classify_failure(message) == ("fail", "intent")


# --- AC 3: faults never fail the suite --------------------------------------------------


def test_internal_failure_warns_once_and_disables_without_a_file(tmp_path):
    def boom_opener(_path):
        raise OSError("disk gone")

    with pytest.warns(NluLedgerWarning) as caught:
        writer = _writer(tmp_path, pid=5, opener=boom_opener)
        writer.start_session("sk-1")
        writer.record_row(locale="it-IT", utterance="x", expected_intent="A", outcome="pass")
        writer.finish()
    assert len(caught) == 1
    assert list(tmp_path.glob("*.jsonl")) == []
    assert len(writer.errors) == 1


def test_identity_reader_failure_records_null_and_continues(tmp_path):
    def boom(locale):
        raise RuntimeError("smapi down")

    with pytest.warns(NluLedgerWarning) as caught:
        writer = _writer(tmp_path, pid=6, identity_reader=boom)
        writer.start_session("sk-1")
        writer.record_row(
            locale="it-IT", utterance="x", expected_intent="A",
            actual_intent="A", outcome="pass",
        )
        writer.finish()
    assert len(caught) == 1
    assert writer.complete is True  # non-fatal: the session still completes
    (session_file,) = tmp_path.glob("*.jsonl")
    records = [json.loads(line) for line in session_file.read_text(encoding="utf-8").splitlines()]
    assert records[1]["live_identity_start"] is None
    assert records[-1]["live_identity_end"] == {"it-IT": None}


# --- AC 4: dry-run, NLU_LEDGER and NLU_LEDGER_DIR ---------------------------------------


def test_dry_run_environment_writes_no_file(tmp_path, monkeypatch):
    monkeypatch.setenv("NLU_LEDGER_DIR", str(tmp_path))
    assert nlu_ledger.writer_for_environment(dry_run=True) is None
    assert list(tmp_path.glob("*.jsonl")) == []


def test_nlu_ledger_zero_disables(tmp_path, monkeypatch):
    monkeypatch.setenv("NLU_LEDGER", "0")
    monkeypatch.setenv("NLU_LEDGER_DIR", str(tmp_path))
    assert nlu_ledger.writer_for_environment() is None
    assert list(tmp_path.glob("*.jsonl")) == []


@pytest.mark.parametrize("value", ["1", "", "yes"])
def test_nlu_ledger_other_values_leave_it_enabled(tmp_path, monkeypatch, value):
    monkeypatch.setenv("NLU_LEDGER", value)
    writer = nlu_ledger.writer_for_environment(out_dir=str(tmp_path))
    assert writer is not None
    assert writer.out_dir == tmp_path


def test_nlu_ledger_dir_redirects_output(tmp_path, monkeypatch):
    target = tmp_path / "redirected"
    monkeypatch.setenv("NLU_LEDGER_DIR", str(target))
    writer = nlu_ledger.writer_for_environment(pid=31)
    assert writer is not None
    writer.start_session("sk-1")
    writer.record_row(locale="it-IT", utterance="x", expected_intent="A",
                      actual_intent="A", outcome="pass")
    writer.finish()
    assert list(target.glob("*.jsonl"))
    assert list(tmp_path.glob("*.jsonl")) == []


def test_default_out_dir_is_the_committed_results_dir(monkeypatch):
    monkeypatch.delenv("NLU_LEDGER_DIR", raising=False)
    writer = nlu_ledger.writer_for_environment()
    assert writer is not None
    assert writer.out_dir == nlu_ledger.DEFAULT_RESULTS_DIR


# --- AC 5: live identity calls, nulls and the two-call budget ---------------------------


def test_identity_reader_called_at_most_twice_per_locale(tmp_path):
    calls: list[str] = []

    def counting(locale):
        calls.append(locale)
        return "etag-9"

    writer = _writer(tmp_path, pid=8, identity_reader=counting)
    writer.start_session("sk-1")
    for index in range(5):
        writer.record_row(locale="it-IT", utterance=f"row {index}", expected_intent="A",
                          actual_intent="A", outcome="pass")
    writer.record_row(locale="es-ES", utterance="fila", expected_intent="A",
                      actual_intent="A", outcome="pass")
    writer.finish()
    assert calls.count("it-IT") == 2
    assert calls.count("es-ES") == 2


def test_identity_reader_is_null_when_unobtainable_and_never_called_when_absent(tmp_path):
    writer = _writer(tmp_path, pid=9, identity_reader=None)
    writer.start_session("sk-1")
    writer.record_row(locale="it-IT", utterance="x", expected_intent="A",
                      actual_intent="A", outcome="pass")
    writer.finish()
    (session_file,) = tmp_path.glob("*.jsonl")
    records = [json.loads(line) for line in session_file.read_text(encoding="utf-8").splitlines()]
    assert records[1]["live_identity_start"] is None
    assert records[-1]["live_identity_end"] == {"it-IT": None}


def test_smapi_client_live_model_identity_parses_etag(monkeypatch):
    payload = json.dumps({
        "interactionModel": {
            "it-IT": {"eTag": "abc123", "lastUpdateRequest": {"status": "SUCCEEDED"}},
        }
    })
    monkeypatch.setattr(smapi_client_module, "_run_ask", lambda args: payload)
    client = SmapiClient(skill_id="sk", locale="it-IT")
    assert client.live_model_identity() == "abc123"


def test_smapi_client_live_model_identity_tolerates_body_wrapper(monkeypatch):
    payload = json.dumps({
        "body": {"interactionModel": {"es-ES": {"eTag": 42}}},
    })
    monkeypatch.setattr(smapi_client_module, "_run_ask", lambda args: payload)
    client = SmapiClient(skill_id="sk", locale="es-ES")
    assert client.live_model_identity() == "42"


def test_smapi_client_live_model_identity_returns_none_on_any_failure(monkeypatch):
    def _raise(_args):
        raise SmapiError("exit 1")

    cases = [
        pytest.param(_raise, id="cli-failure"),
        pytest.param(lambda args: "not json", id="unparseable"),
        pytest.param(lambda args: json.dumps({"interactionModel": {}}), id="missing-locale"),
        pytest.param(lambda args: json.dumps({"interactionModel": {"it-IT": {"eTag": None}}}), id="null-etag"),
        pytest.param(lambda args: json.dumps({"unrelated": True}), id="wrong-shape"),
    ]
    for case in cases:
        monkeypatch.setattr(smapi_client_module, "_run_ask", case.values[0])
        client = SmapiClient(skill_id="sk", locale="it-IT")
        assert client.live_model_identity() is None, case.id


# --- AC 6: the six guidance scenarios ---------------------------------------------------


def test_scenario_stable_row_has_no_flips(tmp_path):
    for pid in (1, 2, 3):
        _run_session(tmp_path, pid=pid,
                     rows=[("disattiva loop", "LoopAllOffIntent", "pass")], minute=pid - 1)
    sessions = nlu_ledger.load_sessions(tmp_path)
    stats = nlu_ledger.compute_stats(sessions)
    (row,) = stats["rows"]
    assert row["runs"] == 3
    assert row["passes"] == 3 and row["fails"] == 0 and row["errors"] == 0
    assert row["actual_intents"] == ["LoopAllOffIntent"]
    assert row["flips_total"] == 0
    assert row["flips_same_model"] == 0
    assert row["last_outcome"] == "pass"
    assert row["last_actual_intent"] == "LoopAllOffIntent"
    assert row["first_session"] != row["last_session"]


def test_scenario_flip_with_same_model_sha_counts_in_both_counters(tmp_path):
    _run_session(tmp_path, pid=1, rows=[("disattiva loop", "LoopAllOffIntent", "pass")], minute=0)
    _run_session(tmp_path, pid=2, rows=[("disattiva loop", "LoopAllOnIntent", "fail")], minute=1)
    sessions = nlu_ledger.load_sessions(tmp_path)
    (row,) = nlu_ledger.compute_stats(sessions)["rows"]
    assert row["flips_total"] == 1
    assert row["flips_same_model"] == 1


def test_scenario_flip_only_when_sha_changed_excluded_from_same_model(tmp_path):
    _run_session(tmp_path, pid=1, rows=[("disattiva loop", "LoopAllOffIntent", "pass")],
                 minute=0, sha="sha-A")
    _run_session(tmp_path, pid=2, rows=[("disattiva loop", "LoopAllOnIntent", "fail")],
                 minute=1, sha="sha-B")
    sessions = nlu_ledger.load_sessions(tmp_path)
    (row,) = nlu_ledger.compute_stats(sessions)["rows"]
    assert row["flips_total"] == 1
    assert row["flips_same_model"] == 0


def test_scenario_error_row_between_two_passes_is_not_a_flip(tmp_path):
    _run_session(tmp_path, pid=1, rows=[("disattiva loop", "LoopAllOffIntent", "pass")], minute=0)
    _run_session(tmp_path, pid=2, rows=[("disattiva loop", None, "error")], minute=1)
    _run_session(tmp_path, pid=3, rows=[("disattiva loop", "LoopAllOffIntent", "pass")], minute=2)
    sessions = nlu_ledger.load_sessions(tmp_path)
    (row,) = nlu_ledger.compute_stats(sessions)["rows"]
    assert row["runs"] == 3
    assert row["errors"] == 1
    assert row["flips_total"] == 0
    assert row["flips_same_model"] == 0


def test_scenario_incomplete_session_counts_rows_and_marks_incomplete(tmp_path):
    _run_session(tmp_path, pid=1, rows=[("disattiva loop", "LoopAllOffIntent", "pass")],
                 minute=0, complete=False)
    _run_session(tmp_path, pid=2, rows=[("disattiva loop", "LoopAllOffIntent", "pass")], minute=1)
    sessions = nlu_ledger.load_sessions(tmp_path)
    assert [s["complete"] for s in sessions] == [False, True]
    stats = nlu_ledger.compute_stats(sessions)
    (row,) = stats["rows"]
    assert row["runs"] == 2  # the incomplete session's rows still count
    assert row["first_session"] == sessions[0]["session_id"]
    assert stats["sessions"][0]["complete"] is False


def test_scenario_rebuild_during_run_is_reported(tmp_path):
    _run_session(tmp_path, pid=1, rows=[("disattiva loop", "LoopAllOffIntent", "pass")],
                 minute=0, identities=["etag-A", "etag-B"])
    sessions = nlu_ledger.load_sessions(tmp_path)
    payload = nlu_ledger.compute_stats(sessions)
    entry = payload["sessions"][0]["locales"]["it-IT"]
    assert entry["rebuilt_during_run"] is True


# --- AC 7: incomplete sessions and the session ordering contract ------------------------


def test_sessions_order_by_started_then_session_id(tmp_path):
    _run_session(tmp_path, pid=20, rows=[("a", "A", "pass")], minute=1)
    _run_session(tmp_path, pid=10, rows=[("b", "B", "pass")], minute=0)
    sessions = nlu_ledger.load_sessions(tmp_path)
    # Same started string on minute ties falls back to the session_id order.
    first, second = sessions
    assert first["started"] <= second["started"]
    if first["started"] == second["started"]:
        assert first["session_id"] < second["session_id"]


def test_row_key_normalizes_the_utterance(tmp_path):
    _run_session(tmp_path, pid=1, rows=[("Disattiva  LOOP", "LoopAllOffIntent", "pass")], minute=0)
    _run_session(tmp_path, pid=2, rows=[("disattiva loop", "LoopAllOffIntent", "pass")], minute=1)
    sessions = nlu_ledger.load_sessions(tmp_path)
    stats = nlu_ledger.compute_stats(sessions)
    assert len(stats["rows"]) == 1
    row = stats["rows"][0]
    assert row["utterance"] == "disattiva loop"
    assert row["runs"] == 2


# --- AC 8: rebuilt_during_run truth table -----------------------------------------------


@pytest.mark.parametrize(
    "identities,want",
    [
        pytest.param(["etag-x", "etag-y"], True, id="both-nonnull-differ"),
        pytest.param(["etag-x", "etag-x"], False, id="both-nonnull-equal"),
        pytest.param([None, "etag-y"], None, id="start-null"),
        pytest.param(["etag-x", None], None, id="end-null"),
        pytest.param([None, None], None, id="both-null"),
    ],
)
def test_rebuilt_during_run_truth_table(tmp_path, identities, want):
    _run_session(tmp_path, pid=1, rows=[("disattiva loop", "A", "pass")], identities=identities)
    sessions = nlu_ledger.load_sessions(tmp_path)
    stats = nlu_ledger.compute_stats(sessions)
    entry = stats["sessions"][0]["locales"]["it-IT"]
    assert entry["rebuilt_during_run"] is want


# --- AC 9: deterministic output ---------------------------------------------------------


def test_stats_output_byte_identical_across_runs(tmp_path, capsys):
    _run_session(tmp_path, pid=1, rows=[("disattiva loop", "A", "pass"),
                                        ("ferma tutto", "B", "fail")], minute=0)
    _run_session(tmp_path, pid=2, rows=[("disattiva loop", "B", "fail"),
                                        ("ferma tutto", "B", "pass")], minute=1,
                 identities=["etag-A", "etag-B"], sha="sha-B")
    code1 = nlu_cli.main(["ledger", "stats", "--dir", str(tmp_path)])
    first_json = capsys.readouterr().out
    code2 = nlu_cli.main(["ledger", "stats", "--dir", str(tmp_path)])
    second_json = capsys.readouterr().out
    assert code1 == 0 and code2 == 0
    assert first_json == second_json and first_json
    code3 = nlu_cli.main(["ledger", "stats", "--dir", str(tmp_path), "--format", "text"])
    first_text = capsys.readouterr().out
    code4 = nlu_cli.main(["ledger", "stats", "--dir", str(tmp_path), "--format", "text"])
    second_text = capsys.readouterr().out
    assert code3 == 0 and code4 == 0
    assert first_text == second_text and first_text


def test_stats_payload_has_no_absolute_paths(tmp_path, capsys):
    _run_session(tmp_path, pid=1, rows=[("disattiva loop", "A", "pass")], minute=0)
    nlu_cli.main(["ledger", "stats", "--dir", str(tmp_path)])
    out = capsys.readouterr().out
    assert str(tmp_path) not in out
    assert str(REPO) not in out


# --- AC 10: the results directory is gitignored -----------------------------------------


def test_results_dir_is_gitignored():
    gitignore = (REPO / ".gitignore").read_text(encoding="utf-8")
    assert "tests/integration/results/" in gitignore


# --- AC 11: the doc page ----------------------------------------------------------------


def test_docs_document_the_ledger_contract():
    doc = (REPO / "docs" / "nlu-static-analysis.md").read_text(encoding="utf-8")
    assert "tests/integration/results" in doc
    assert "session_start" in doc and "locale_start" in doc
    assert "session_end" in doc
    assert "flips_total" in doc and "flips_same_model" in doc
    assert "trainer noise" in doc
    assert "model_sha256" in doc
    assert "ledger stats" in doc and "ledger list" in doc


# --- AC 12: expected_slots stored verbatim ----------------------------------------------


def test_expected_slots_verbatim_shapes(tmp_path):
    writer = _writer(tmp_path, pid=10)
    writer.start_session("sk-1")
    # No slot expectations: the empty object (the wiring passes {} for absent).
    writer.record_row(locale="it-IT", utterance="a", expected_intent="A",
                      actual_intent="A", outcome="pass")
    # Any-value expectations: the empty object, verbatim.
    writer.record_row(locale="it-IT", utterance="b", expected_intent="A",
                      expected_slots={}, actual_intent="A", outcome="pass")
    # Pinned values: the pinned object, verbatim.
    pinned = {"series_name": {"value": "adolescencia"}, "season_number": {}}
    writer.record_row(locale="es-ES", utterance="c", expected_intent="A",
                      expected_slots=pinned, actual_intent="A", outcome="pass")
    # The bare-string shorthand some fixtures use: also verbatim.
    writer.record_row(locale="es-ES", utterance="d", expected_intent="A",
                      expected_slots={"song_name": "immagine"}, actual_intent="A",
                      outcome="pass")
    writer.finish()
    rows = []
    (session_file,) = tmp_path.glob("*.jsonl")
    for line in session_file.read_text(encoding="utf-8").splitlines():
        record = json.loads(line)
        if record["type"] == "row":
            rows.append(record)
    assert rows[0]["expected_slots"] == {}
    assert rows[1]["expected_slots"] == {}
    assert rows[2]["expected_slots"] == pinned
    assert rows[3]["expected_slots"] == {"song_name": "immagine"}


def test_row_record_field_set(tmp_path):
    """The full field set the suite writes for one row, asserted from the file."""
    writer = _writer(tmp_path, pid=11)
    writer.start_session("sk-1")
    writer.record_row(
        locale="es-ES",
        utterance="pon adolescencia temporada uno episodio tres",
        expected_intent="PlayEpisodeIntent",
        expected_slots={"series_name": {"value": "adolescencia"}, "season_number": {}},
        actual_intent="PlaySongIntent",
        slots={"series_name": "adolescencia"},
        outcome="fail",
        failure_kind="intent",
        detail="Intent mismatch for 'pon adolescencia...' (es-ES)",
    )
    writer.finish()
    (session_file,) = tmp_path.glob("*.jsonl")
    rows = [json.loads(line) for line in session_file.read_text(encoding="utf-8").splitlines()]
    row = [r for r in rows if r["type"] == "row"][-1]
    assert set(row) == {
        "type", "locale", "utterance", "expected_intent", "expected_slots",
        "actual_intent", "slots", "outcome", "failure_kind", "detail",
    }
    assert row["locale"] == "es-ES"
    assert row["utterance"] == "pon adolescencia temporada uno episodio tres"
    assert row["expected_intent"] == "PlayEpisodeIntent"
    assert row["actual_intent"] == "PlaySongIntent"
    assert row["outcome"] == "fail"
    assert row["failure_kind"] == "intent"


# --- the observation bridge --------------------------------------------------------------


def test_bridge_observe_flattens_slots_and_is_defensive():
    import nlu_ledger_bridge

    nlu_ledger_bridge.reset()
    nlu_ledger_bridge.observe("PlaySongIntent", {"song": {"name": "song", "value": "x"}})
    intent, slots = nlu_ledger_bridge.take()
    assert intent == "PlaySongIntent"
    assert slots == {"song": "x"}
    assert nlu_ledger_bridge.take() == (None, None)  # take() resets
    # A malformed payload must never raise into the test body.
    nlu_ledger_bridge.observe("A", [("not", "a", "dict")])
    nlu_ledger_bridge.observe(None, None)
    intent, slots = nlu_ledger_bridge.take()
    assert intent is None
    assert slots == {}


# --- the CLI paths -----------------------------------------------------------------------


def test_cli_ledger_stats_and_list(tmp_path, capsys):
    _run_session(tmp_path, pid=1, rows=[("disattiva loop", "A", "pass")], minute=0)
    _run_session(tmp_path, pid=2, rows=[("disattiva loop", "B", "fail")], minute=1,
                 complete=False)
    code = nlu_cli.main(["ledger", "stats", "--dir", str(tmp_path)])
    payload = json.loads(capsys.readouterr().out)
    assert code == 0
    assert len(payload["sessions"]) == 2
    assert payload["sessions"][1]["complete"] is False
    (row,) = payload["rows"]
    assert row["flips_total"] == 1
    assert row["last_outcome"] == "fail"
    code = nlu_cli.main(["ledger", "list", "--dir", str(tmp_path)])
    listing = capsys.readouterr().out
    assert code == 0
    assert "complete=no" in listing
    assert "rebuilt: it-IT=null" in listing


def test_cli_ledger_stats_filters(tmp_path, capsys):
    _run_session(tmp_path, pid=1, rows=[("disattiva loop", "A", "pass")], minute=0)
    _run_session(tmp_path, pid=2, rows=[("disattiva loop", "B", "fail")], minute=1)
    _run_session(tmp_path, pid=3, rows=[("disattiva loop", "A", "pass")], minute=2)
    code = nlu_cli.main(["ledger", "stats", "--dir", str(tmp_path), "--last", "2"])
    payload = json.loads(capsys.readouterr().out)
    assert code == 0
    assert len(payload["sessions"]) == 2
    assert payload["rows"][0]["runs"] == 2
    code = nlu_cli.main(["ledger", "stats", "--dir", str(tmp_path), "--last", "0"])
    assert code == 2
    assert "error" in capsys.readouterr().err
    code = nlu_cli.main(["ledger", "stats", "--dir", str(tmp_path), "--locale", "es-ES"])
    payload = json.loads(capsys.readouterr().out)
    assert code == 0
    assert payload["rows"] == []
    # With a locale, the sessions are first restricted to those that
    # exercised it: no es-ES session, no session listed.
    assert payload["sessions"] == []


def test_cli_ledger_text_and_json_forms_agree(tmp_path, capsys):
    _run_session(tmp_path, pid=1, rows=[("disattiva loop", "A", "pass")], minute=0)
    nlu_cli.main(["ledger", "stats", "--dir", str(tmp_path)])
    payload = json.loads(capsys.readouterr().out)
    nlu_cli.main(["ledger", "stats", "--dir", str(tmp_path), "--format", "text"])
    text = capsys.readouterr().out
    assert f"row keys: {len(payload['rows'])}" in text
    assert f"sessions: {len(payload['sessions'])}" in text


def test_cli_ledger_missing_dir_is_a_clean_error(tmp_path, capsys):
    code = nlu_cli.main(["ledger", "stats", "--dir", str(tmp_path / "absent")])
    assert code == 2
    assert "error" in capsys.readouterr().err
    code = nlu_cli.main(["ledger", "list", "--dir", str(tmp_path / "absent")])
    assert code == 2
    assert "error" in capsys.readouterr().err


def test_locale_filter_precedes_the_last_slice(tmp_path, capsys):
    # it-IT runs daily; es-ES runs monthly. --locale es-ES --last 1 must mean
    # the newest es-ES session, not the newest session of any locale.
    _run_session(tmp_path, pid=1, rows=[("disattiva loop", "A", "pass")], minute=0)
    _run_session(tmp_path, pid=2, rows=[("disattiva loop", "B", "fail")], minute=1)
    _run_session(tmp_path, pid=3, rows=[("pon breaking bad", "C", "pass")], minute=2)
    # Retag the third session as es-ES by rewriting its records' locale.
    (session_file,) = tmp_path.glob("nlu-*120200Z-3.jsonl")
    text = session_file.read_text(encoding="utf-8").replace(
        '"locale": "it-IT"', '"locale": "es-ES"')
    session_file.write_text(text, encoding="utf-8")
    code = nlu_cli.main([
        "ledger", "stats", "--dir", str(tmp_path), "--locale", "es-ES", "--last", "1",
    ])
    payload = json.loads(capsys.readouterr().out)
    assert code == 0
    assert len(payload["sessions"]) == 1
    assert payload["sessions"][0]["session_id"] == "nlu-20261124T120200Z-3"
    assert payload["rows"][0]["utterance"] == "pon breaking bad"


def test_foreign_jsonl_files_are_not_sessions(tmp_path, capsys):
    _run_session(tmp_path, pid=1, rows=[("disattiva loop", "A", "pass")], minute=0)
    (tmp_path / "foreign-export.jsonl").write_text(
        json.dumps({"type": "session_start"}) + "\n", encoding="utf-8")
    code = nlu_cli.main(["ledger", "list", "--dir", str(tmp_path)])
    listing = capsys.readouterr().out
    assert code == 0
    assert "foreign-export" not in listing
    assert listing.count("complete=") == 1


def test_ledger_module_avoids_the_em_dash():
    em_dash = chr(0x2014)  # built from the codepoint: the character never lands in this file
    module = SCRIPTS_DIR / "nlu_static" / "ledger.py"
    assert em_dash not in module.read_text(encoding="utf-8")
