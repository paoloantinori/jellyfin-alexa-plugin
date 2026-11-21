"""The read-only NLU Lab webapp: page-state composition (run list with per-run
expectation-failure counts, the change-driven matrix for two chosen runs, the
intent legend from the newer run), the it/en chrome-strings loader, and a
stdlib HTTP server that serves ONLY the whitelisted static files under web/
plus the /state.json endpoint.

Read-only over the runs dir: nothing here deploys, writes run files, or calls
SMAPI. Corrupt run files surface as clearly-badged rows (runs.RunLoadError is
caught per file), never as a traceback page. The same-locale guarantee the
compare CLI only warns about is enforced here: the run list is filtered to the
requested locale, so a cross-locale pair cannot be assembled.
"""
import http.server
import json
import urllib.parse
from pathlib import Path

import _paths
_paths.bootstrap()

from runs import RunLoadError, RunRecord, load_run

WEB_DIR = Path(__file__).resolve().parent / "web"
DEFAULT_PORT = 8765

STRINGS_FILES = {"en": "strings.en.json", "it": "strings.it.json"}

# The complete set of servable files: a whitelist, not a directory walk, so
# nothing outside web/ (and nothing added to web/ later) is ever reachable.
_STATIC_FILES = {
    "/": "index.html",
    "/index.html": "index.html",
    "/app.js": "app.js",
    "/strings.en.json": "strings.en.json",
    "/strings.it.json": "strings.it.json",
}
_CONTENT_TYPES = {".html": "text/html; charset=utf-8",
                  ".js": "text/javascript; charset=utf-8",
                  ".json": "application/json; charset=utf-8"}


class StateRefused(ValueError):
    """The requested page state cannot be composed (blank locale, unknown or
    corrupt run id). The HTTP layer turns this into a 400 + error JSON."""


def load_strings(lang: str | None, web_dir: Path | None = None) -> dict:
    """Chrome strings for the requested language: the localized file when the
    language has one, with MISSING keys falling back to en key-by-key; en (and
    any unknown language) resolves to the en file alone."""
    web_dir = Path(web_dir) if web_dir is not None else WEB_DIR
    en = json.loads((web_dir / STRINGS_FILES["en"]).read_text(encoding="utf-8"))
    if lang not in STRINGS_FILES or lang == "en":
        return en
    localized = json.loads((web_dir / STRINGS_FILES[lang]).read_text(encoding="utf-8"))
    return {**en, **localized}


def _resolve_lang(lang: str | None, locale: str) -> str:
    if lang in STRINGS_FILES:
        return lang
    prefix = locale.split("-", 1)[0]
    return prefix if prefix in STRINGS_FILES else "en"


def _scan_runs(runs_dir: Path, locale: str) -> tuple[list[dict], dict[str, RunRecord]]:
    """One list entry per run file plus the parsed records of the readable ones.
    Newest first ((timestamp, id) descending). Entries are filtered to the
    requested locale; corrupt files stay in the list as corrupt=True badge rows
    (their locale is unknowable, so they are never filtered and never usable)."""
    entries: list[dict] = []
    records: dict[str, RunRecord] = {}
    for path in sorted(Path(runs_dir).glob("*.json")):
        try:
            run = load_run(path)
        except RunLoadError as exc:
            entries.append({"id": path.stem, "label": path.stem, "timestamp": None,
                            "locale": None, "status": "corrupt", "corrupt": True,
                            "expectation_failures": None, "error": str(exc)})
            continue
        if run.locale != locale:
            continue
        entries.append({"id": run.id, "label": run.label, "timestamp": run.timestamp,
                        "locale": run.locale, "status": run.status, "corrupt": False,
                        "expectation_failures": run.expectation_failure_count,
                        "error": run.error})
        records[run.id] = run
    entries.sort(key=lambda e: (e["timestamp"] or "", e["id"]), reverse=True)
    return entries, records


def _pair_warnings(records: dict, a_entry, b_entry) -> list[str]:
    """Incomparability signals for the chosen pair (mirrors the CLI compare
    guards): different models, different builds, different skills. The
    change-driven matrix stays renderable, but the page says when the pair
    cannot support a before/after reading."""
    if not (a_entry and b_entry):
        return []
    a, b = records[a_entry["id"]], records[b_entry["id"]]
    warnings = []
    if a.model_sha256 != b.model_sha256:
        warnings.append(f"model sha mismatch: {a.model_sha256[:12]} vs {b.model_sha256[:12]}")
    if a.build_etag and b.build_etag and a.build_etag != b.build_etag:
        warnings.append(f"different builds (eTag {a.build_etag} vs {b.build_etag})")
    if a.skill_id != b.skill_id:
        warnings.append(f"different lab skills: {a.skill_id} vs {b.skill_id}")
    return warnings


def _resolve_pair(runs_dir: Path, entries: list[dict],
                  run_a: str | None, run_b: str | None) -> tuple[dict | None, dict | None]:
    """Resolve the baseline (a) and the new (b) run entries. Explicit ids must
    name a readable run OF THE REQUESTED LOCALE (the entries list is already
    locale-filtered, which is what enforces the same-locale guarantee). A
    missing side defaults to the newest readable run other than the given one;
    with nothing given, the two newest are taken."""
    by_id = {e["id"]: e for e in entries}
    readable = [e for e in entries if not e["corrupt"]]

    def resolve(run_id: str) -> dict:
        entry = by_id.get(run_id)
        if entry is None:
            raise StateRefused(
                f"run '{run_id}' is not a run of this locale under {runs_dir} "
                "(pass a run id from the list)")
        if entry["corrupt"]:
            raise StateRefused(
                f"run '{run_id}' is unreadable ({entry['error']}); it cannot be compared")
        return entry

    run_a, run_b = run_a or None, run_b or None  # bare "" from a query string
    if run_a is None and run_b is None:
        if len(readable) >= 2:
            run_a, run_b = readable[1]["id"], readable[0]["id"]
        elif len(readable) == 1:
            run_a = run_b = readable[0]["id"]
    else:
        if run_b is None:
            run_b = next((e["id"] for e in readable if e["id"] != run_a), None)
        if run_a is None:
            run_a = next((e["id"] for e in readable if e["id"] != run_b), None)
    return (resolve(run_a) if run_a else None,
            resolve(run_b) if run_b else None)


def _side(row) -> dict | None:
    if row is None:
        return None
    return {"selected_intent": row.selected_intent, "status": row.status.value,
            "slots": row.slots, "considered_intents": row.considered_intents}


def build_page_state(runs_dir: Path, locale: str, run_a: str | None = None,
                     run_b: str | None = None, lang: str | None = None,
                     web_dir: Path | None = None) -> dict:
    """Everything one page view needs in one JSON blob: the run list, the
    change-driven matrix for the chosen pair (union keyed by (utterance,
    source), kinds from RunRecord.diff_against, both sides' detail), the legend
    from the newer run's intent_roster (run_b by convention), and the resolved
    chrome strings. Raises StateRefused on a blank locale or an id that is
    unknown, corrupt, or of another locale."""
    if not locale or not locale.strip():
        raise StateRefused("a locale is required (e.g. it-IT)")
    entries, records = _scan_runs(Path(runs_dir), locale)
    a_entry, b_entry = _resolve_pair(Path(runs_dir), entries, run_a, run_b)

    matrix: list[dict] = []
    legend: list[dict] = []
    changed = 0
    if a_entry and b_entry:
        old, new = records[a_entry["id"]], records[b_entry["id"]]
        old_rows = {(r.utterance, r.source): r for r in old.results}
        new_rows = {(r.utterance, r.source): r for r in new.results}
        for d in new.diff_against(old):
            key = (d.utterance, d.source)
            prev, cur = old_rows.get(key), new_rows.get(key)
            matrix.append({"utterance": d.utterance, "source": d.source,
                           "kind": d.kind,
                           "expected_intent": (cur or prev).expected_intent,
                           "old": _side(prev), "new": _side(cur)})
            if d.kind != "same":
                changed += 1
        matrix.sort(key=lambda r: (r["utterance"], r["source"]))
        legend = list(new.intent_roster)

    resolved_lang = _resolve_lang(lang, locale)
    return {
        "meta": {"locale": locale, "lang": resolved_lang,
                 "pair_warnings": _pair_warnings(records, a_entry, b_entry),
                 "run_a": a_entry["id"] if a_entry else None,
                 "run_b": b_entry["id"] if b_entry else None,
                 "label_a": a_entry["label"] if a_entry else None,
                 "label_b": b_entry["label"] if b_entry else None,
                 "changed": changed, "total": len(matrix)},
        "runs": entries,
        "matrix": matrix,
        "legend": legend,
        "strings": load_strings(resolved_lang, web_dir=web_dir),
    }


def _make_handler(runs_dir: Path, web_dir: Path) -> type:
    class LabRequestHandler(http.server.BaseHTTPRequestHandler):
        # a read-only lab tool: per-request stderr lines would only spam the
        # console the server is sharing with the operator
        def log_message(self, format, *args):  # noqa: A002
            pass

        def do_GET(self):  # noqa: N802
            parsed = urllib.parse.urlsplit(self.path)
            path = urllib.parse.unquote(parsed.path)
            if path == "/state.json":
                self._serve_state(parsed.query)
                return
            if ".." in path.split("/"):
                self._send(403, "text/plain; charset=utf-8", b"refused: path traversal\n")
                return
            name = _STATIC_FILES.get(path)
            if name is None:
                self._send(404, "text/plain; charset=utf-8", b"not found\n")
                return
            self._send_file(web_dir / name)

        def _serve_state(self, query: str) -> None:
            params = urllib.parse.parse_qs(query)
            locale = (params.get("locale") or [""])[0]
            run_a = (params.get("runA") or [None])[0]
            run_b = (params.get("runB") or [None])[0]
            lang = (params.get("lang") or [None])[0]
            try:
                state = build_page_state(runs_dir, locale, run_a=run_a, run_b=run_b,
                                         lang=lang, web_dir=web_dir)
            except StateRefused as exc:
                body = json.dumps({"error": str(exc)}).encode("utf-8")
                self._send(400, "application/json; charset=utf-8", body)
                return
            self._send(200, "application/json; charset=utf-8",
                       json.dumps(state, ensure_ascii=False).encode("utf-8"))

        def _send_file(self, path: Path) -> None:
            try:
                body = path.read_bytes()
            except OSError:
                self._send(404, "text/plain; charset=utf-8", b"not found\n")
                return
            self._send(200, _CONTENT_TYPES.get(path.suffix, "application/octet-stream"),
                       body)

        def _send(self, status: int, content_type: str, body: bytes) -> None:
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

    return LabRequestHandler


def make_server(runs_dir: Path, web_dir: Path | None = None, host: str = "0.0.0.0",
                port: int = DEFAULT_PORT) -> http.server.ThreadingHTTPServer:
    """Build (but do not start) the server. Tests bind 127.0.0.1 on port 0;
    serve() binds the LAN-facing default."""
    web_dir = Path(web_dir) if web_dir is not None else WEB_DIR
    return http.server.ThreadingHTTPServer(
        (host, port), _make_handler(Path(runs_dir), web_dir))


def serve(runs_dir: Path, port: int = DEFAULT_PORT, host: str = "0.0.0.0",
          web_dir: Path | None = None) -> None:
    """Serve the read-only webapp until Ctrl-C. Binds 0.0.0.0 by default so the
    page opens from a phone on the same LAN; read-only over runs_dir."""
    httpd = make_server(runs_dir, web_dir=web_dir, host=host, port=port)
    print(f"nlu-lab webapp: http://{host}:{port}/ (runs under {runs_dir}); Ctrl-C to stop")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        httpd.server_close()
