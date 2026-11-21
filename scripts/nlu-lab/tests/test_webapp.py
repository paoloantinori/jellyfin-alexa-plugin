"""The read-only webapp: build_page_state (run list with per-run expectation
failure counts, the change-driven matrix for two chosen runs, the intent legend
from the newer run), the chrome-strings loader (it picked when requested, en
fallback key-by-key), and the HTTP layer (ONLY the static files under web/ plus
/state.json; path traversal refused; StateRefused becomes a 400 error JSON,
never a traceback page)."""
import http.client
import json
import threading
from pathlib import Path

import pytest

import _paths
_paths.bootstrap()

import cli
import webapp
from runs import RunRecord, RowResult, RowStatus, save_run

WEB_DIR = Path(webapp.__file__).resolve().parent / "web"


def _row(utterance, selected, expected="PlaySongIntent", source="fixture",
         status=RowStatus.OK, slots=None, considered=None):
    return RowResult(utterance=utterance, source=source, status=status,
                     expected_intent=expected, selected_intent=selected,
                     slots=slots or {}, considered_intents=considered or [],
                     raw={})


def _save_run(runs_dir, run_id, results, *, locale="it-IT", label=None,
              timestamp="2026-11-20T10:00:00", roster=None):
    run = RunRecord(id=run_id, timestamp=timestamp, locale=locale,
                    label=label or run_id, model_source="m", model_sha256="0" * 64,
                    skill_id="s", status="ok", intent_roster=roster or [],
                    results=results)
    return save_run(runs_dir, run)


# --- build_page_state: the run list ---

def test_run_list_carries_label_timestamp_and_failure_counts(tmp_path):
    runs_dir = tmp_path / "runs"
    _save_run(runs_dir, "old", [_row("a", "PlaySongIntent"),
                                _row("b", None, status=RowStatus.NO_SELECTION)],
              timestamp="2026-11-19T09:00:00")
    _save_run(runs_dir, "new", [_row("a", "PlaySongIntent")],
              timestamp="2026-11-20T10:00:00", label="fix r2")
    _save_run(runs_dir, "de", [_row("a", "PlaySongIntent")], locale="de-DE",
              timestamp="2026-11-21T10:00:00")
    state = webapp.build_page_state(runs_dir, "it-IT")
    assert [r["id"] for r in state["runs"]] == ["new", "old"]  # newest first, de-DE excluded
    entry = state["runs"][0]
    assert entry["label"] == "fix r2" and entry["timestamp"] == "2026-11-20T10:00:00"
    assert entry["expectation_failures"] == 0 and entry["corrupt"] is False
    assert state["runs"][1]["expectation_failures"] == 1  # the no_selection row


def test_corrupt_run_file_is_a_badged_row_not_a_traceback(tmp_path):
    runs_dir = tmp_path / "runs"
    _save_run(runs_dir, "good", [_row("a", "PlaySongIntent")])
    (runs_dir / "broken.json").write_text("[1, 2, 3]", encoding="utf-8")
    (runs_dir / "empty.json").write_text("{}", encoding="utf-8")
    state = webapp.build_page_state(runs_dir, "it-IT")
    by_id = {r["id"]: r for r in state["runs"]}
    assert by_id["good"]["corrupt"] is False
    for broken in ("broken", "empty"):
        assert by_id[broken]["corrupt"] is True
        assert f"{broken}.json" in by_id[broken]["error"]


def test_default_pair_is_the_two_newest_runs_of_the_locale(tmp_path):
    runs_dir = tmp_path / "runs"
    _save_run(runs_dir, "r1", [_row("a", "PlaySongIntent")], timestamp="2026-11-18T09:00:00")
    _save_run(runs_dir, "r2", [_row("a", "PlaySongIntent")], timestamp="2026-11-19T09:00:00")
    _save_run(runs_dir, "r3", [_row("a", "PlaySongIntent")], timestamp="2026-11-20T09:00:00")
    state = webapp.build_page_state(runs_dir, "it-IT")
    assert state["meta"]["run_a"] == "r2" and state["meta"]["run_b"] == "r3"


# --- build_page_state: the matrix and the legend ---

def test_matrix_union_kinds_sides_and_legend(tmp_path):
    runs_dir = tmp_path / "runs"
    _save_run(runs_dir, "stock", [
        _row("to-green", "PlayRadioIntent"),
        _row("to-red", "MediaInfoIntent"),
        _row("to-amber", "MediaInfoIntent", expected=None),
        _row("stays", "PlaySongIntent"),
        _row("booms", "PlaySongIntent"),
        _row("gone", "PlaySongIntent"),
    ], timestamp="2026-11-19T09:00:00", label="base")
    _save_run(runs_dir, "fix", [
        _row("to-green", "PlaySongIntent", slots={"song": "nuvole bianche"},
             considered=["PlaySongIntent", "PlayRadioIntent"]),
        _row("to-red", "PlayRadioIntent"),
        _row("to-amber", "PlaySongIntent", expected=None),
        _row("stays", "PlaySongIntent"),
        _row("booms", None, status=RowStatus.ERROR, expected=None),
        _row("added", "PlaySongIntent"),
    ], timestamp="2026-11-20T10:00:00", label="fix",
        roster=[{"intent": "PlaySongIntent", "sample": "suona il brano x"},
                {"intent": "PlayRadioIntent", "sample": "metti la radio x"}])
    state = webapp.build_page_state(runs_dir, "it-IT", run_a="stock", run_b="fix")
    by_key = {(r["utterance"], r["source"]): r for r in state["matrix"]}
    kinds = {u: r["kind"] for (u, s), r in by_key.items()}
    assert kinds == {"to-green": "green", "to-red": "red", "to-amber": "amber",
                     "stays": "same", "booms": "error", "gone": "absent_old",
                     "added": "absent_new"}
    row = by_key[("to-green", "fixture")]
    assert row["old"]["selected_intent"] == "PlayRadioIntent"
    assert row["new"]["selected_intent"] == "PlaySongIntent"
    assert row["new"]["slots"] == {"song": "nuvole bianche"}
    assert row["new"]["considered_intents"] == ["PlaySongIntent", "PlayRadioIntent"]
    assert row["expected_intent"] == "PlaySongIntent"
    assert by_key[("gone", "fixture")]["new"] is None      # only in the baseline
    assert by_key[("added", "fixture")]["old"] is None     # only in the new run
    # the legend is the NEWER run's roster (run_b by convention)
    assert state["legend"] == [{"intent": "PlaySongIntent", "sample": "suona il brano x"},
                               {"intent": "PlayRadioIntent", "sample": "metti la radio x"}]
    assert state["meta"]["changed"] == 6 and state["meta"]["total"] == 7


def test_state_refusals_unknown_corrupt_and_cross_locale(tmp_path):
    runs_dir = tmp_path / "runs"
    _save_run(runs_dir, "it", [_row("a", "PlaySongIntent")], locale="it-IT")
    _save_run(runs_dir, "de", [_row("a", "PlaySongIntent")], locale="de-DE")
    (runs_dir / "broken.json").write_text("[1]", encoding="utf-8")
    # a de-DE run id is not in the it-IT run list: the webapp enforces the
    # same-locale guarantee the compare CLI only warns about
    with pytest.raises(webapp.StateRefused):
        webapp.build_page_state(runs_dir, "it-IT", run_a="it", run_b="de")
    with pytest.raises(webapp.StateRefused):
        webapp.build_page_state(runs_dir, "it-IT", run_a="nope", run_b="it")
    with pytest.raises(webapp.StateRefused):
        webapp.build_page_state(runs_dir, "it-IT", run_a="broken", run_b="it")
    with pytest.raises(webapp.StateRefused):
        webapp.build_page_state(runs_dir, "  ")


# --- the strings loader ---

def test_strings_loader_picks_it_and_falls_back_key_by_key(tmp_path):
    (tmp_path / "strings.en.json").write_text(
        json.dumps({"a": "one", "b": "two"}), encoding="utf-8")
    (tmp_path / "strings.it.json").write_text(
        json.dumps({"a": "uno"}), encoding="utf-8")
    assert webapp.load_strings("it", web_dir=tmp_path) == {"a": "uno", "b": "two"}
    assert webapp.load_strings("en", web_dir=tmp_path) == {"a": "one", "b": "two"}
    assert webapp.load_strings("xx", web_dir=tmp_path) == {"a": "one", "b": "two"}


def test_shipped_strings_files_share_one_key_set():
    en = json.loads((WEB_DIR / "strings.en.json").read_text(encoding="utf-8"))
    it = json.loads((WEB_DIR / "strings.it.json").read_text(encoding="utf-8"))
    assert set(en) == set(it)  # fallback exists for robustness, not for shipping gaps


def test_state_carries_resolved_strings_and_lang(tmp_path):
    runs_dir = tmp_path / "runs"
    _save_run(runs_dir, "r1", [_row("a", "X")], timestamp="2026-11-19T09:00:00")
    _save_run(runs_dir, "r2", [_row("a", "PlaySongIntent")], timestamp="2026-11-20T09:00:00")
    state = webapp.build_page_state(runs_dir, "it-IT")
    assert state["meta"]["lang"] == "it"          # derived from the locale prefix
    assert state["strings"]["before"] == "prima"
    state = webapp.build_page_state(runs_dir, "it-IT", lang="en")
    assert state["meta"]["lang"] == "en" and state["strings"]["before"] == "before"


# --- the HTTP layer (a real server on an ephemeral loopback port) ---

def _get(port, raw_path):
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
    try:
        conn.request("GET", raw_path)  # verbatim path: http.client does not normalize
        resp = conn.getresponse()
        return resp.status, resp.read().decode("utf-8", "replace")
    finally:
        conn.close()


def test_http_serves_static_state_and_refuses_traversal(tmp_path):
    runs_dir = tmp_path / "runs"
    _save_run(runs_dir, "r1", [_row("a", "MediaInfoIntent")], timestamp="2026-11-19T09:00:00")
    _save_run(runs_dir, "r2", [_row("a", "PlaySongIntent")], timestamp="2026-11-20T09:00:00")
    (runs_dir / "broken.json").write_text("[1]", encoding="utf-8")
    httpd = webapp.make_server(runs_dir, web_dir=WEB_DIR, host="127.0.0.1", port=0)
    thread = threading.Thread(target=httpd.serve_forever, daemon=True)
    thread.start()
    try:
        port = httpd.server_address[1]
        status, body = _get(port, "/")
        assert status == 200 and "app.js" in body and "matrix" in body
        status, body = _get(port, "/app.js")
        assert status == 200 and "state.json" in body
        status, body = _get(port, "/strings.it.json")
        assert status == 200 and json.loads(body)["before"] == "prima"
        status, body = _get(port, "/state.json?locale=it-IT")
        assert status == 200
        state = json.loads(body)
        assert state["meta"]["run_a"] == "r1" and state["meta"]["run_b"] == "r2"
        assert state["matrix"][0]["kind"] == "green"
        # only web/ files and the state endpoint are served; traversal is refused
        assert _get(port, "/../lab.json")[0] == 403
        assert _get(port, "/web/../runs/r1.json")[0] == 403
        assert _get(port, "/%2e%2e/lab.json")[0] == 403
        assert _get(port, "/etc/passwd")[0] == 404
        assert _get(port, "/favicon.ico")[0] == 404
        status, body = _get(port, "/state.json?locale=it-IT&runA=nope&runB=r1")
        assert status == 400 and "nope" in json.loads(body)["error"]
        status, body = _get(port, "/state.json?locale=it-IT&runA=broken&runB=r1")
        assert status == 400 and "broken" in json.loads(body)["error"]
    finally:
        httpd.shutdown()
        httpd.server_close()
        thread.join(timeout=5)


# --- cli serve wiring ---

def test_serve_subcommand_wiring(tmp_path, monkeypatch):
    seen = {}

    def fake_serve(runs_dir, port):
        seen["runs_dir"], seen["port"] = runs_dir, port

    monkeypatch.setattr(webapp, "serve", fake_serve)
    args = cli.build_parser().parse_args(["serve", "--lab-dir", str(tmp_path)])
    assert args.port == 8765
    assert cli.cmd_serve(args) == 0
    assert seen == {"runs_dir": tmp_path / "runs", "port": 8765}
    args = cli.build_parser().parse_args(
        ["serve", "--lab-dir", str(tmp_path), "--port", "9000"])
    assert cli.cmd_serve(args) == 0 and seen["port"] == 9000
