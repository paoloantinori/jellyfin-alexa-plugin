"""The read-only webapp: build_page_state (run list with per-run expectation
failure counts, the change-driven matrix for two chosen runs, the intent legend
from the newer run), the chrome-strings loader (it picked when requested, en
fallback key-by-key), and the HTTP layer (ONLY the static files under web/ plus
/state.json; path traversal refused; StateRefused becomes a 400 error JSON,
never a traceback page)."""
import http.client
import json
import os
import shutil
import subprocess
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
         status=RowStatus.OK, slots=None, considered=None, error=None):
    return RowResult(utterance=utterance, source=source, status=status,
                     expected_intent=expected, selected_intent=selected,
                     slots=slots or {}, considered_intents=considered or [],
                     raw={}, error=error)


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
        # real slot shape: SmapiClient.parse_profile_nlu_result emits
        # {slot_name: {"name": ..., "value": ...}}, never flat strings
        _row("to-green", "PlaySongIntent",
             slots={"song": {"name": "song", "value": "nuvole bianche"}},
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
    assert row["new"]["slots"] == {"song": {"name": "song", "value": "nuvole bianche"}}
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


def test_matrix_sides_carry_row_error_text(tmp_path):
    """JF-975: the side projection must not drop RowResult.error: the diff UI
    surfaces WHY a row errored (the ask CLI's stderr rides it); None on
    healthy rows."""
    runs_dir = tmp_path / "runs"
    _save_run(runs_dir, "stock", [
        _row("boom", "PlaySongIntent"),
        _row("fine", "PlaySongIntent"),
    ], timestamp="2026-11-19T09:00:00")
    _save_run(runs_dir, "fix", [
        _row("boom", None, status=RowStatus.ERROR, expected=None,
             error="ask smapi profile-nlu exited 1: Token has expired (boom)"),
        _row("fine", "PlaySongIntent"),
    ], timestamp="2026-11-20T10:00:00")
    state = webapp.build_page_state(runs_dir, "it-IT", run_a="stock", run_b="fix")
    by_key = {(r["utterance"], r["source"]): r for r in state["matrix"]}
    assert by_key[("boom", "fixture")]["new"]["error"] == \
        "ask smapi profile-nlu exited 1: Token has expired (boom)"
    assert by_key[("boom", "fixture")]["old"]["error"] is None
    assert by_key[("fine", "fixture")]["new"]["error"] is None


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


# --- app.js behavior (the real script executed under node with a DOM stub) ---

_JS_PRELUDE = """
const _els = {};
class _El {
  constructor(tag) {
    this.tag = tag; this.children = []; this.textContent = "";
    this.hidden = false; this.value = ""; this.selected = false;
    this.disabled = false; this.className = ""; this.dataset = {};
    this.classList = { add() {}, remove() {}, toggle() {} };
  }
  appendChild(child) { this.children.push(child); return child; }
}
const document = {
  title: "",
  getElementById(id) { return _els[id] || (_els[id] = new _El(id)); },
  createElement(tag) { return new _El(tag); },
  createTextNode(text) { return { text }; },
  querySelectorAll() { return []; },
};
const _store = {};
if (process.env.LAB_STORED_LANG) _store["nlu-lab-lang"] = process.env.LAB_STORED_LANG;
const localStorage = {
  getItem(k) { return k in _store ? _store[k] : null; },
  setItem(k, v) { _store[k] = String(v); },
};
const navigator = { language: process.env.LAB_NAV_LANG || "en-US" };
const location = { search: "?locale=it-IT" };
let _lastFetchUrl = null;
const fetch = async (url) => {
  _lastFetchUrl = url;
  return { ok: true, status: 200,
           json: async () => JSON.parse(process.env.LAB_STATE_JSON) };
};
function _collect(node) {
  if (!node) return "";
  if (typeof node.text === "string") return node.text;
  return (typeof node.textContent === "string" ? node.textContent : "") +
    (node.children || []).map(_collect).join("");
}
"""

_JS_DRIVER = """
(async () => {
  await refresh();
  const query = new URLSearchParams((_lastFetchUrl || "").split("?")[1] || "");
  const out = { langParam: query.get("lang") };
  const row = S.state.matrix.find((r) => r.new && r.new.slots &&
    Object.keys(r.new.slots).length > 0);
  if (row) {
    S.selected = row;
    renderDetail(S.state);
    out.detailHidden = document.getElementById("detail").hidden;
    out.detailText = _collect(document.getElementById("detail-body"));
  }
  console.log(JSON.stringify(out));
})();
"""

_JS_ERROR_DRIVER = """
(async () => {
  await refresh();
  const out = {};
  const row = S.state.matrix.find((r) => r.new && r.new.status === "error");
  if (row) {
    S.selected = row;
    renderDetail(S.state);
    out.detailHidden = document.getElementById("detail").hidden;
    out.detailText = _collect(document.getElementById("detail-body"));
    // the error badge's tooltip: each matrix row is [utterance, old, new]
    for (const tr of document.getElementById("matrix-body").children) {
      const utt = tr.children[0] && tr.children[0].children[0]
        ? tr.children[0].children[0].textContent : "";
      if (utt === row.utterance) out.newBadgeTitle = tr.children[2].title || null;
    }
  }
  console.log(JSON.stringify(out));
})();
"""


def _run_app_js(tmp_path, state, stored_lang=None, nav_lang="it-IT",
                driver=_JS_DRIVER):
    """Run the real web/app.js under node (minimal DOM stub; the composed page
    state is served through the fetch stub) and return the driver's JSON: the
    lang param app.js sent and the rendered detail text. Skipped when no node
    is on PATH (the CI runner ships one)."""
    node = shutil.which("node")
    if node is None:
        pytest.skip("node is not available to execute web/app.js")
    script = tmp_path / "app_js_harness.js"
    script.write_text(
        _JS_PRELUDE + "\n" + (WEB_DIR / "app.js").read_text(encoding="utf-8")
        + "\n" + driver, encoding="utf-8")
    env = {**os.environ,
           "LAB_STATE_JSON": json.dumps(state, ensure_ascii=False),
           "LAB_STORED_LANG": stored_lang or "",
           "LAB_NAV_LANG": nav_lang}
    proc = subprocess.run([node, str(script)], capture_output=True, text=True,
                          env=env, timeout=60)
    assert proc.returncode == 0, proc.stderr
    return json.loads(proc.stdout.strip().splitlines()[-1])


def test_app_js_detail_renders_slot_value_not_object(tmp_path):
    """The detail card must render the slot VALUE: slots arrive as
    {name, value} objects (the shape parse_profile_nlu_result stores in run
    files), and the old `k + ": " + v` concatenation showed [object Object]."""
    runs_dir = tmp_path / "runs"
    _save_run(runs_dir, "r1", [_row("suona a", "X")], timestamp="2026-11-19T09:00:00")
    _save_run(runs_dir, "r2", [_row("suona a", "PlaySongIntent",
                                    slots={"song": {"name": "song", "value": "nuvole bianche"}})],
              timestamp="2026-11-20T09:00:00")
    state = webapp.build_page_state(runs_dir, "it-IT", run_a="r1", run_b="r2")
    out = _run_app_js(tmp_path, state)
    assert out["detailHidden"] is False
    assert "song: nuvole bianche" in out["detailText"]
    assert "[object Object]" not in out["detailText"]


def test_app_js_renders_error_text_on_badge_and_detail(tmp_path):
    """JF-975: an error row's failure text (the ask CLI's stderr) reaches the
    human surfaces: a native tooltip on the error badge and a full detail
    line, inserted as text (createTextNode), never HTML."""
    err_text = "ask smapi profile-nlu exited 1: Token has expired (boom)"
    runs_dir = tmp_path / "runs"
    _save_run(runs_dir, "r1", [_row("boom", "PlaySongIntent")],
              timestamp="2026-11-19T09:00:00")
    _save_run(runs_dir, "r2", [_row("boom", None, status=RowStatus.ERROR,
                                    expected=None, error=err_text)],
              timestamp="2026-11-20T09:00:00")
    state = webapp.build_page_state(runs_dir, "it-IT", run_a="r1", run_b="r2")
    out = _run_app_js(tmp_path, state, driver=_JS_ERROR_DRIVER)
    assert out["detailHidden"] is False
    assert "errore" in out["detailText"]        # the localized detail_error label
    assert err_text in out["detailText"]        # the full text, once on the new side
    assert out["newBadgeTitle"] == err_text     # the badge tooltip


@pytest.mark.parametrize("stored,nav,expected", [
    ("it", "en-US", "it"),   # the stored manual override wins
    (None, "it-IT", "it"),   # browser language with a supported prefix
    (None, "en-US", "en"),
    (None, "fr-FR", "en"),   # unsupported browser language falls back to en
])
def test_app_js_lang_default_follows_browser_language(tmp_path, stored, nav, expected):
    """Chrome language spec: stored override, else the browser's language when
    supported (en/it), else en; the resolved lang is always SENT to
    /state.json, so the server never guesses from the run locale."""
    runs_dir = tmp_path / "runs"
    _save_run(runs_dir, "r1", [_row("a", "X")], timestamp="2026-11-19T09:00:00")
    _save_run(runs_dir, "r2", [_row("a", "PlaySongIntent")], timestamp="2026-11-20T09:00:00")
    state = webapp.build_page_state(runs_dir, "it-IT", run_a="r1", run_b="r2")
    out = _run_app_js(tmp_path, state, stored_lang=stored, nav_lang=nav)
    assert out["langParam"] == expected


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
