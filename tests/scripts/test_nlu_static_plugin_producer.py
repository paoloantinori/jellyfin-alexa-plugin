"""JF-954.8: the --from-plugin authoritative producer battery.

The plugin export endpoint returns the exact per-locale catalog payloads the
sync would upload; this battery pins the producer function, the CLI wiring
and the key-safety contract. No test reaches the network, SMAPI, the ask CLI
or Jellyfin: every HTTP touch goes through the same injectable seam the
JF-954.1 producer introduced (producer_jellyfin._http_get_json and the
fetch_json parameter), replaced with fakes here.
"""

import json
import sys
import urllib.parse
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

from nlu_static import cli as nlu_cli  # noqa: E402
from nlu_static import producer_jellyfin  # noqa: E402
from nlu_static.effective_model import load_snapshot  # noqa: E402

API_KEY = "sekret-123"
VALID_SNAPSHOT = {
    "schema": 1,
    "producer": "plugin",
    "locales": {
        "es-ES": {"SeriesName": [{"value": "Adolescencia", "synonyms": ["los Breakin Bad"]}]},
        "it-IT": {
            "JellyfinArtist": [{"value": "Soul Coughing", "synonyms": ["Sol Cofin"]}],
            "SeriesName": [{"value": "Dark", "synonyms": []}],
        },
    },
}


def _capture_fetch(payload: object, calls: list):
    """A fake seam that records (url, api_key) per call and returns the payload."""
    def fake(url: str, api_key: str):
        calls.append((url, api_key))
        return json.loads(json.dumps(payload))

    return fake


def _snapshot_doc(payload: object) -> dict:
    return json.loads(json.dumps(payload))


# --- the producer function -------------------------------------------------------------


def test_from_plugin_returns_the_snapshot_and_uses_the_endpoint_path():
    calls: list = []
    snapshot = producer_jellyfin.build_snapshot_from_plugin(
        "http://jf.test:8096", API_KEY, "0123abcd-0000-1111-2222-333344445555",
        fetch_json=_capture_fetch(_snapshot_doc(VALID_SNAPSHOT), calls),
    )
    assert snapshot == VALID_SNAPSHOT
    assert len(calls) == 1
    url, key = calls[0]
    assert key == API_KEY
    assert url.startswith("http://jf.test:8096/Plugins/AlexaSkill/Diagnostics/CatalogSnapshot?")
    query = urllib.parse.parse_qs(urllib.parse.urlparse(url).query)
    assert query["userId"] == ["0123abcd-0000-1111-2222-333344445555"]
    assert "locale" not in query


def test_from_plugin_locale_flag_rides_the_query():
    calls: list = []
    producer_jellyfin.build_snapshot_from_plugin(
        "http://jf.test", API_KEY, "u-1", locale="es-ES",
        fetch_json=_capture_fetch(_snapshot_doc(VALID_SNAPSHOT), calls),
    )
    query = urllib.parse.parse_qs(urllib.parse.urlparse(calls[0][0]).query)
    assert query["locale"] == ["es-ES"]


def test_from_plugin_never_puts_the_key_in_the_url():
    calls: list = []
    producer_jellyfin.build_snapshot_from_plugin(
        "http://jf.test", API_KEY, "u-1",
        fetch_json=_capture_fetch(_snapshot_doc(VALID_SNAPSHOT), calls),
    )
    assert all(API_KEY not in url for url, _ in calls)
    assert all("ApiKey=" not in url for url, _ in calls)


def test_from_plugin_rejects_a_non_plugin_producer():
    bogus = _snapshot_doc(VALID_SNAPSHOT)
    bogus["producer"] = "jellyfin-names"
    with pytest.raises(ValueError, match="producer"):
        producer_jellyfin.build_snapshot_from_plugin(
            "http://jf.test", API_KEY, "u-1",
            fetch_json=_capture_fetch(bogus, []),
        )


def test_from_plugin_rejects_an_invalid_snapshot():
    bogus = _snapshot_doc(VALID_SNAPSHOT)
    bogus["schema"] = 2
    with pytest.raises(ValueError, match="schema"):
        producer_jellyfin.build_snapshot_from_plugin(
            "http://jf.test", API_KEY, "u-1",
            fetch_json=_capture_fetch(bogus, []),
        )


def test_from_plugin_rejects_a_non_object_body():
    with pytest.raises(ValueError, match="non-object"):
        producer_jellyfin.build_snapshot_from_plugin(
            "http://jf.test", API_KEY, "u-1",
            fetch_json=_capture_fetch([1, 2], []),
        )


def test_from_plugin_output_passes_load_snapshot(tmp_path):
    calls: list = []
    snapshot = producer_jellyfin.build_snapshot_from_plugin(
        "http://jf.test", API_KEY, "u-1",
        fetch_json=_capture_fetch(_snapshot_doc(VALID_SNAPSHOT), calls),
    )
    path = tmp_path / "roundtrip.json"
    path.write_text(json.dumps(snapshot), encoding="utf-8")
    assert load_snapshot(path) == snapshot


def test_from_plugin_real_seam_sends_the_mediabrowser_header(monkeypatch):
    """The real seam (not a fake) builds the Authorization header; the key
    never rides the URL. Same contract the name-level producer pins."""
    captured: dict = {}

    class _Resp:
        def __init__(self, body: bytes):
            self._body = body

        def read(self) -> bytes:
            return self._body

        def __enter__(self):
            return self

        def __exit__(self, *_exc):
            return False

    def fake_urlopen(request, timeout=None):
        captured["url"] = request.full_url
        captured["headers"] = dict(request.header_items())
        return _Resp(json.dumps(VALID_SNAPSHOT).encode("utf-8"))

    monkeypatch.setattr(producer_jellyfin.urllib.request, "urlopen", fake_urlopen)
    snapshot = producer_jellyfin.build_snapshot_from_plugin("http://jf.test", API_KEY, "u-1")
    assert snapshot == VALID_SNAPSHOT
    assert API_KEY not in captured["url"]
    auth = {k.lower(): v for k, v in captured["headers"].items()}
    assert auth.get("authorization") == f'MediaBrowser Token="{API_KEY}"'


# --- the CLI wiring ---------------------------------------------------------------------


def test_cli_from_plugin_requires_user_id(monkeypatch, capsys):
    monkeypatch.setenv("JELLYFIN_URL", "http://jf.test")
    monkeypatch.setenv("JELLYFIN_API_KEY", API_KEY)
    code = nlu_cli.main(["snapshot", "--from-plugin"])
    assert code == 2
    captured = capsys.readouterr()
    assert "--user-id" in captured.err
    assert "error" in captured.err


def test_cli_from_plugin_never_prints_or_writes_the_key(tmp_path, monkeypatch, capsys):
    calls: list = []
    monkeypatch.setattr(
        producer_jellyfin, "_http_get_json", _capture_fetch(_snapshot_doc(VALID_SNAPSHOT), calls)
    )
    out_path = tmp_path / "snap.json"
    code = nlu_cli.main(
        [
            "snapshot",
            "--url", "http://jf.test:8096",
            "--api-key", API_KEY,
            "--from-plugin",
            "--user-id", "u-1",
            "--out", str(out_path),
        ]
    )
    assert code == 0
    captured = capsys.readouterr()
    assert API_KEY not in captured.out
    assert API_KEY not in captured.err
    written = out_path.read_text(encoding="utf-8")
    assert API_KEY not in written
    assert '"producer": "plugin"' in written
    assert calls and all(API_KEY not in url for url, _ in calls)


def test_cli_from_plugin_roundtrip_validates_and_is_deterministic(tmp_path, monkeypatch, capsys):
    calls: list = []
    monkeypatch.setattr(
        producer_jellyfin, "_http_get_json", _capture_fetch(_snapshot_doc(VALID_SNAPSHOT), calls)
    )
    out_path = tmp_path / "snap.json"
    first = nlu_cli.main(
        ["snapshot", "--url", "http://jf.test", "--api-key", API_KEY, "--from-plugin", "--user-id", "u-1", "--out", str(out_path)]
    )
    first_bytes = out_path.read_bytes()
    second = nlu_cli.main(
        ["snapshot", "--url", "http://jf.test", "--api-key", API_KEY, "--from-plugin", "--user-id", "u-1", "--out", str(out_path)]
    )
    second_bytes = out_path.read_bytes()
    assert first == 0 and second == 0
    assert first_bytes == second_bytes
    # The written file validates through the format's own entry point.
    assert load_snapshot(out_path) == VALID_SNAPSHOT


def test_cli_from_plugin_with_locale_restricts_the_export(monkeypatch, capsys):
    calls: list = []
    monkeypatch.setattr(
        producer_jellyfin, "_http_get_json", _capture_fetch(_snapshot_doc(VALID_SNAPSHOT), calls)
    )
    code = nlu_cli.main(
        ["snapshot", "--url", "http://jf.test", "--api-key", API_KEY, "--from-plugin", "--user-id", "u-1", "--locale", "es-ES"]
    )
    assert code == 0
    query = urllib.parse.parse_qs(urllib.parse.urlparse(calls[0][0]).query)
    assert query["locale"] == ["es-ES"]


def test_cli_from_plugin_refuses_types(monkeypatch, capsys):
    monkeypatch.setenv("JELLYFIN_URL", "http://jf.test")
    monkeypatch.setenv("JELLYFIN_API_KEY", API_KEY)
    code = nlu_cli.main(["snapshot", "--from-plugin", "--user-id", "u-1", "--types", "Series"])
    assert code == 2
    assert "mutually exclusive" in capsys.readouterr().err


def test_cli_locale_without_from_plugin_is_refused(monkeypatch, capsys):
    monkeypatch.setenv("JELLYFIN_URL", "http://jf.test")
    monkeypatch.setenv("JELLYFIN_API_KEY", API_KEY)
    code = nlu_cli.main(["snapshot", "--locale", "es-ES"])
    assert code == 2
    assert "--from-plugin" in capsys.readouterr().err


def test_cli_user_id_without_from_plugin_is_refused(monkeypatch, capsys):
    """A forgotten --from-plugin must refuse, not silently produce the
    name-level snapshot under the given --user-id (review round symmetry)."""
    monkeypatch.setenv("JELLYFIN_URL", "http://jf.test")
    monkeypatch.setenv("JELLYFIN_API_KEY", API_KEY)
    code = nlu_cli.main(["snapshot", "--user-id", "u-1"])
    assert code == 2
    captured = capsys.readouterr()
    assert "--user-id" in captured.err
    assert "--from-plugin" in captured.err


def test_cli_from_plugin_redacts_failures(monkeypatch, capsys):
    def boom(url: str, api_key: str):
        raise RuntimeError(f"GET {url} exploded")

    monkeypatch.setattr(producer_jellyfin, "_http_get_json", boom)
    code = nlu_cli.main(
        ["snapshot", "--url", "http://jf.test", "--api-key", API_KEY, "--from-plugin", "--user-id", "u-1"]
    )
    assert code == 2
    captured = capsys.readouterr()
    assert API_KEY not in captured.out
    assert API_KEY not in captured.err
    assert "error" in captured.err


def test_cli_flag_errors_come_before_connection_errors(monkeypatch, capsys):
    """A mis-flagged invocation reports the flag diagnostic even when no
    connection could be resolved; the connection error must not mask it."""
    monkeypatch.delenv("JELLYFIN_URL", raising=False)
    monkeypatch.delenv("JELLYFIN_API_KEY", raising=False)
    code = nlu_cli.main(["snapshot", "--from-plugin", "--types", "Series"])
    assert code == 2
    assert "mutually exclusive" in capsys.readouterr().err


def test_from_plugin_empty_export_is_a_named_error(monkeypatch, capsys):
    """The plugin honestly emits an empty table when the sync would upload
    nothing for the user; the producer names that case instead of the
    generic schema error (the format refuses empty tables)."""
    empty = {"schema": 1, "producer": "plugin", "locales": {}}
    calls: list = []
    monkeypatch.setattr(
        producer_jellyfin, "_http_get_json", _capture_fetch(empty, calls)
    )
    code = nlu_cli.main(
        ["snapshot", "--url", "http://jf.test", "--api-key", API_KEY, "--from-plugin", "--user-id", "u-1"]
    )
    assert code == 2
    captured = capsys.readouterr()
    assert "no locale entries" in captured.err
    assert "error" in captured.err
    assert API_KEY not in captured.err
