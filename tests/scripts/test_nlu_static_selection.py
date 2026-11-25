"""JF-954.7: canonical model identity and change-driven row selection.

Identity tests are pure dict tests (plus one formatting test through files);
select tests pass synthetic old and new models as dicts and build their
fixture rows INLINE, never through a committed fixture file. The CLI-level
byte-identity tests read the committed es-ES model against HEAD, the
real-data touch the parent task allows (read only). The conftest hook tests
import the integration conftest module directly and feed it fake items, so
the abort path is proven without collecting a single real test. No test
reaches the network, SMAPI or the ask CLI.
"""

import importlib.util
import json
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest
from _pytest.outcomes import Exit as PytestExit

REPO = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

from nlu_static import cli as nlu_cli  # noqa: E402
from nlu_static import selection as nlu_selection  # noqa: E402
from nlu_static.effective_model import unwrap_language_model  # noqa: E402
from nlu_static.normalize import normalize_text  # noqa: E402

ES_ES = "es-ES"


# ---------------------------------------------------------------- helpers


def _model(intents, types=None, invocation="test") -> dict:
    """A bare languageModel: name -> values lists for the type blocks."""
    return {
        "invocationName": invocation,
        "intents": intents,
        "types": [
            {"name": name, "values": [{"name": {"value": value}} for value in values]}
            for name, values in (types or {}).items()
        ],
    }


def _model_raw(intents, types, invocation="test") -> dict:
    """A bare languageModel whose types carry raw value/synonym entries."""
    return {
        "invocationName": invocation,
        "intents": intents,
        "types": [
            {
                "name": name,
                "values": [
                    {"name": {"value": value, "synonyms": list(synonyms)}}
                    for value, synonyms in entries
                ],
            }
            for name, entries in types.items()
        ],
    }


def _intent(name, samples, slots=()) -> dict:
    """One intent block: samples plus (name, type) slot pairs."""
    return {
        "name": name,
        "samples": samples,
        "slots": [{"name": slot_name, "type": slot_type} for slot_name, slot_type in slots],
    }


def _rows(*pairs) -> list[dict]:
    """Inline fixture rows from (utterance, expected_intent) pairs."""
    return [{"utterance": utterance, "expected_intent": expected} for utterance, expected in pairs]


def _write_fixture_yaml(directory: Path, locale: str, pairs) -> Path:
    """One minimal fixture file, written as JSON (valid YAML) for determinism."""
    payload = {
        "locale": locale,
        "invocation_name": "test",
        "tests": [{"utterance": u, "expected_intent": e} for u, e in pairs],
    }
    path = directory / f"{locale}.yaml"
    path.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    return path


# ------------------------------------------------- Part A: identity hashes


def test_identity_ignores_key_order_and_file_formatting(tmp_path):
    model = {
        "invocationName": "x",
        "intents": [{"name": "A", "samples": ["uno"], "slots": []}],
        "types": [{"name": "T", "values": [{"name": {"value": "v"}}]}],
    }
    wrapped = {"interactionModel": {"languageModel": model}}
    reordered = {
        "types": [{"values": [{"name": {"value": "v"}}], "name": "T"}],
        "intents": [{"slots": [], "samples": ["uno"], "name": "A"}],
        "invocationName": "x",
    }
    # Different key order at every level and a wrapper vs bare shape: equal.
    assert nlu_selection.model_sha256(model) == nlu_selection.model_sha256(reordered)
    assert nlu_selection.model_sha256(model) == nlu_selection.model_sha256(
        wrapped["interactionModel"]["languageModel"]
    )
    # The same dict serialized with different whitespace parses equal.
    pretty = tmp_path / "pretty.json"
    compact = tmp_path / "compact.json"
    pretty.write_text(json.dumps(wrapped, indent=4), encoding="utf-8")
    compact.write_text(json.dumps(wrapped, separators=(",", ":")), encoding="utf-8")
    parsed = [
        unwrap_language_model(json.loads(path.read_text(encoding="utf-8")))
        for path in (pretty, compact)
    ]
    assert nlu_selection.model_sha256(parsed[0]) == nlu_selection.model_sha256(parsed[1])


def test_identity_changes_on_sample_slot_value_synonym_and_sample_order():
    base = _model_raw(
        [_intent("AlphaIntent", ["a {x}", "b {x}"], slots=[("x", "T")])],
        {"T": [("v1", ())]},
    )
    base_sha = nlu_selection.model_sha256(base)
    variants = {
        "changed sample": _model_raw(
            [_intent("AlphaIntent", ["a2 {x}", "b {x}"], slots=[("x", "T")])],
            {"T": [("v1", ())]},
        ),
        "new sample": _model_raw(
            [_intent("AlphaIntent", ["a {x}", "b {x}", "c {x}"], slots=[("x", "T")])],
            {"T": [("v1", ())]},
        ),
        "reordered samples": _model_raw(
            [_intent("AlphaIntent", ["b {x}", "a {x}"], slots=[("x", "T")])],
            {"T": [("v1", ())]},
        ),
        "changed slot type": _model_raw(
            [_intent("AlphaIntent", ["a {x}", "b {x}"], slots=[("x", "U")])],
            {"T": [("v1", ())], "U": [("u1", ())]},
        ),
        "changed slot name": _model_raw(
            [_intent("AlphaIntent", ["a {x}", "b {x}"], slots=[("y", "T")])],
            {"T": [("v1", ())]},
        ),
        "added slot": _model_raw(
            [
                _intent(
                    "AlphaIntent",
                    ["a {x}", "b {x}"],
                    slots=[("x", "T"), ("z", "AMAZON.NUMBER")],
                )
            ],
            {"T": [("v1", ())]},
        ),
        "changed type value": _model_raw(
            [_intent("AlphaIntent", ["a {x}", "b {x}"], slots=[("x", "T")])],
            {"T": [("v2", ())]},
        ),
        "added type value": _model_raw(
            [_intent("AlphaIntent", ["a {x}", "b {x}"], slots=[("x", "T")])],
            {"T": [("v1", ()), ("v2", ())]},
        ),
        "changed synonym": _model_raw(
            [_intent("AlphaIntent", ["a {x}", "b {x}"], slots=[("x", "T")])],
            {"T": [("v1", ("s1",))]},
        ),
        "changed invocation": _model_raw(
            [_intent("AlphaIntent", ["a {x}", "b {x}"], slots=[("x", "T")])],
            {"T": [("v1", ())]},
            invocation="other",
        ),
    }
    for label, variant in variants.items():
        assert nlu_selection.model_sha256(variant) != base_sha, label


def test_identity_cli_byte_identical_and_json_shape(capsys):
    # The committed es-ES model hashes identically across two runs and the
    # document carries the pinned schema and field names.
    argv = ["identity", "--locale", ES_ES]
    rc = nlu_cli.main(argv)
    out = capsys.readouterr().out
    nlu_cli.main(argv)
    out_again = capsys.readouterr().out
    assert rc == 0
    assert out == out_again
    doc = json.loads(out)
    assert doc["schema"] == nlu_selection.IDENTITY_SCHEMA
    entry = doc["locales"][ES_ES]
    assert len(entry["committed_sha256"]) == 64
    assert "effective_sha256" not in entry  # no snapshot given


def test_identity_cli_snapshot_adds_effective_sha256(tmp_path, capsys):
    snap_path = tmp_path / "snap.json"
    snap_path.write_text(
        json.dumps({"schema": 1, "locales": {"*": {"SeriesName": [{"value": "x"}]}}}),
        encoding="utf-8",
    )
    rc = nlu_cli.main(["identity", "--locale", ES_ES, "--snapshot", str(snap_path)])
    doc = json.loads(capsys.readouterr().out)
    assert rc == 0
    entry = doc["locales"][ES_ES]
    assert len(entry["effective_sha256"]) == 64
    # Replacing the es-ES SeriesName seed with one foreign value changes the
    # effective hash.
    assert entry["effective_sha256"] != entry["committed_sha256"]


def test_snapshot_replaced_model_rules():
    raw = {
        "invocationName": "x",
        "intents": [
            {"name": "AlphaIntent", "samples": ["s {sn}"], "slots": [{"name": "sn", "type": "SeriesName"}]}
        ],
        "types": [
            {"name": "SeriesName", "values": [{"name": {"value": "Seed"}}]},
            {"name": "StaticT", "values": [{"name": {"value": "static"}}]},
        ],
    }
    snap_unsorted = {"schema": 1, "locales": {ES_ES: {"SeriesName": [{"value": "b"}, {"value": "a"}, {"value": "c"}]}}}
    effective = nlu_selection.snapshot_replaced_model(raw, snap_unsorted, ES_ES)
    series = next(t for t in effective["types"] if t["name"] == "SeriesName")
    assert [v["name"]["value"] for v in series["values"]] == ["a", "b", "c"]  # entries sorted
    static = next(t for t in effective["types"] if t["name"] == "StaticT")
    assert [v["name"]["value"] for v in static["values"]] == ["static"]  # STATIC untouched
    assert [v["name"]["value"] for v in raw["types"][0]["values"]] == ["Seed"]  # input not mutated
    # Snapshot entry order does not change the effective hash.
    snap_sorted = {"schema": 1, "locales": {ES_ES: {"SeriesName": [{"value": "a"}, {"value": "b"}, {"value": "c"}]}}}
    assert nlu_selection.model_sha256(effective) == nlu_selection.model_sha256(
        nlu_selection.snapshot_replaced_model(raw, snap_sorted, ES_ES)
    )
    # The "*" fallback applies when the locale has no entry of its own.
    snap_star = {"schema": 1, "locales": {"*": {"SeriesName": [{"value": "z"}]}}}
    effective_star = nlu_selection.snapshot_replaced_model(raw, snap_star, ES_ES)
    series_star = next(t for t in effective_star["types"] if t["name"] == "SeriesName")
    assert [v["name"]["value"] for v in series_star["values"]] == ["z"]
    # A snapshot naming a STATIC type is ignored: the effective hash stays
    # the committed hash.
    snap_static = {"schema": 1, "locales": {ES_ES: {"StaticT": [{"value": "hax"}]}}}
    ignored = nlu_selection.snapshot_replaced_model(raw, snap_static, ES_ES)
    assert nlu_selection.model_sha256(ignored) == nlu_selection.model_sha256(raw)


# ------------------------------------------------- Part B: selection


def test_select_unchanged_locale_selects_nothing():
    model = _model([_intent("AlphaIntent", ["uno"])])
    rows = _rows(("uno", "AlphaIntent"))
    result = nlu_selection.select_locale(ES_ES, model, model, rows)
    assert result.payload == {"status": "unchanged"}
    assert result.rows_selected == 0
    assert result.rows_total == 1


def test_select_changed_sample_selects_that_intent():
    old = _model([_intent("AlphaIntent", ["uno"]), _intent("BetaIntent", ["dos"])])
    new = _model([_intent("AlphaIntent", ["uno", "tre"]), _intent("BetaIntent", ["dos"])])
    rows = _rows(("uno", "AlphaIntent"), ("dos", "BetaIntent"))
    result = nlu_selection.select_locale(ES_ES, old, new, rows)
    assert result.payload["status"] == "changed"
    assert result.payload["changed_intents"] == ["AlphaIntent"]
    assert result.payload["neighbour_intents"] == []
    assert result.payload["rows"] == ["uno"]
    assert result.rows_selected == 1


def test_select_added_intent_selects_its_rows():
    old = _model([_intent("AlphaIntent", ["uno"])])
    new = _model([_intent("AlphaIntent", ["uno"]), _intent("GammaIntent", ["tre"])])
    rows = _rows(("uno", "AlphaIntent"), ("tre", "GammaIntent"))
    result = nlu_selection.select_locale(ES_ES, old, new, rows)
    assert result.payload["changed_intents"] == ["GammaIntent"]
    assert result.payload["rows"] == ["tre"]


def test_select_removed_intent_still_selects_its_rows():
    old = _model([_intent("AlphaIntent", ["uno"]), _intent("GammaIntent", ["tre"])])
    new = _model([_intent("AlphaIntent", ["uno"])])
    rows = _rows(("uno", "AlphaIntent"), ("tre", "GammaIntent"))
    result = nlu_selection.select_locale(ES_ES, old, new, rows)
    assert "GammaIntent" in result.payload["changed_intents"]
    assert result.payload["rows"] == ["tre"]


def test_select_changed_type_selects_every_intent_using_it():
    old = _model(
        [
            _intent("AlphaIntent", ["alpha {x}"], slots=[("x", "T")]),
            _intent("BetaIntent", ["beta {y}"], slots=[("y", "U")]),
            _intent("GammaIntent", ["gamma"]),
        ],
        types={"T": ["v1"], "U": ["u1"]},
    )
    new = _model(
        [
            _intent("AlphaIntent", ["alpha {x}"], slots=[("x", "T")]),
            _intent("BetaIntent", ["beta {y}"], slots=[("y", "U")]),
            _intent("GammaIntent", ["gamma"]),
        ],
        types={"T": ["v1", "v2"], "U": ["u1"]},
    )
    rows = _rows(
        ("alpha v1", "AlphaIntent"),
        ("beta u1", "BetaIntent"),
        ("gamma", "GammaIntent"),
    )
    result = nlu_selection.select_locale(ES_ES, old, new, rows)
    assert result.payload["changed_intents"] == ["AlphaIntent"]
    assert result.payload["rows"] == ["alpha v1"]


def test_select_added_type_with_no_consumer_falls_back():
    # Type U is new but no intent declares it: the hashes differ while the
    # changed-intent set stays empty, so selecting nothing would silently
    # drop coverage and the safety rule applies instead.
    old = _model([_intent("AlphaIntent", ["alpha {x}"], slots=[("x", "T")])], types={"T": ["v1"]})
    new = _model([_intent("AlphaIntent", ["alpha {x}"], slots=[("x", "T")])], types={"T": ["v1"], "U": ["u1"]})
    rows = _rows(("alpha v1", "AlphaIntent"))
    result = nlu_selection.select_locale(ES_ES, old, new, rows)
    assert result.payload["status"] == "fallback_all"
    assert result.payload["rows"] == ["alpha v1"]


def test_select_unattributable_hash_change_falls_back():
    # A changed type no intent uses: hashes differ, no intent-level change.
    old = _model([], types={"T": ["v1"]})
    new = _model([], types={"T": ["v1", "v2"]})
    rows = _rows(("uno", "AlphaIntent"))
    result = nlu_selection.select_locale(ES_ES, old, new, rows)
    assert result.payload["status"] == "fallback_all"
    assert "reason" in result.payload
    assert result.payload["rows"] == ["uno"]


def test_select_neighbour_through_old_graph_edge():
    # Old: identical FREE skeletons on both intents, an IDENTICAL edge pair
    # in the OLD graph. New: Alpha's sample changes to a different opener,
    # which breaks the edge, so Beta is reachable only through the old graph.
    old = _model(
        [
            _intent("AlphaIntent", ["riproduci {x}"], slots=[("x", "AMAZON.SearchQuery")]),
            _intent("BetaIntent", ["riproduci {y}"], slots=[("y", "AMAZON.SearchQuery")]),
        ]
    )
    new = _model(
        [
            _intent("AlphaIntent", ["ascolta {x}"], slots=[("x", "AMAZON.SearchQuery")]),
            _intent("BetaIntent", ["riproduci {y}"], slots=[("y", "AMAZON.SearchQuery")]),
        ]
    )
    rows = _rows(("riproduci casa", "BetaIntent"), ("ascolta casa", "AlphaIntent"))
    result = nlu_selection.select_locale(ES_ES, old, new, rows)
    assert result.payload["status"] == "changed"
    assert result.payload["changed_intents"] == ["AlphaIntent"]
    assert result.payload["neighbour_intents"] == ["BetaIntent"]
    assert sorted(result.payload["rows"]) == ["ascolta casa", "riproduci casa"]


def test_select_neighbour_through_new_graph_edge():
    # Old: different openers, no edge. New: Beta changes onto Alpha's opener,
    # creating an identical-skeleton edge in the NEW graph, so Alpha is
    # reachable only through the new graph.
    old = _model(
        [
            _intent("AlphaIntent", ["riproduci {x}"], slots=[("x", "AMAZON.SearchQuery")]),
            _intent("BetaIntent", ["ascolta {y}"], slots=[("y", "AMAZON.SearchQuery")]),
        ]
    )
    new = _model(
        [
            _intent("AlphaIntent", ["riproduci {x}"], slots=[("x", "AMAZON.SearchQuery")]),
            _intent("BetaIntent", ["riproduci {y}"], slots=[("y", "AMAZON.SearchQuery")]),
        ]
    )
    rows = _rows(("riproduci casa", "AlphaIntent"), ("ascolta casa", "BetaIntent"))
    result = nlu_selection.select_locale(ES_ES, old, new, rows)
    assert result.payload["status"] == "changed"
    assert result.payload["changed_intents"] == ["BetaIntent"]
    assert result.payload["neighbour_intents"] == ["AlphaIntent"]
    assert sorted(result.payload["rows"]) == ["ascolta casa", "riproduci casa"]


def test_select_no_neighbours_removes_them():
    old = _model(
        [
            _intent("AlphaIntent", ["riproduci {x}"], slots=[("x", "AMAZON.SearchQuery")]),
            _intent("BetaIntent", ["riproduci {y}"], slots=[("y", "AMAZON.SearchQuery")]),
        ]
    )
    new = _model(
        [
            _intent("AlphaIntent", ["ascolta {x}"], slots=[("x", "AMAZON.SearchQuery")]),
            _intent("BetaIntent", ["riproduci {y}"], slots=[("y", "AMAZON.SearchQuery")]),
        ]
    )
    rows = _rows(("riproduci casa", "BetaIntent"), ("ascolta casa", "AlphaIntent"))
    result = nlu_selection.select_locale(ES_ES, old, new, rows, neighbours=False)
    assert result.payload["changed_intents"] == ["AlphaIntent"]
    assert result.payload["neighbour_intents"] == []
    assert result.payload["rows"] == ["ascolta casa"]


def test_select_invocation_change_selects_every_intent():
    old = _model([_intent("AlphaIntent", ["uno"]), _intent("BetaIntent", ["dos"])], invocation="prima")
    new = _model([_intent("AlphaIntent", ["uno"]), _intent("BetaIntent", ["dos"])], invocation="dopo")
    rows = _rows(("uno", "AlphaIntent"), ("dos", "BetaIntent"))
    result = nlu_selection.select_locale(ES_ES, old, new, rows)
    assert result.payload["status"] == "changed"
    assert result.payload["changed_intents"] == ["AlphaIntent", "BetaIntent"]
    assert result.payload["rows"] == ["dos", "uno"]


def test_select_missing_at_base_counts_fully_changed():
    new = _model([_intent("AlphaIntent", ["uno"]), _intent("BetaIntent", ["dos"])])
    rows = _rows(("uno", "AlphaIntent"), ("dos", "BetaIntent"))
    result = nlu_selection.select_locale(ES_ES, None, new, rows)
    assert result.payload["status"] == "changed"
    assert result.payload["changed_intents"] == ["AlphaIntent", "BetaIntent"]
    assert "note" in result.payload and "base" in result.payload["note"]
    assert result.payload["rows"] == ["dos", "uno"]


def test_select_fallback_all_on_unreadable_base():
    old = {"intents": 42}  # a JSON dict whose shape build_effective_model rejects
    new = _model([_intent("AlphaIntent", ["uno"]), _intent("BetaIntent", ["dos"])])
    rows = _rows(("uno", "AlphaIntent"), ("dos", "BetaIntent"))
    result = nlu_selection.select_locale(ES_ES, old, new, rows)
    assert result.payload["status"] == "fallback_all"
    assert "reason" in result.payload
    assert result.payload["changed_intents"] == []
    assert result.payload["rows"] == ["dos", "uno"]
    assert result.rows_selected == 2


def test_select_fallback_all_on_unbuildable_working_tree():
    old = _model([_intent("AlphaIntent", ["uno"])])
    new = {"invocationName": "test", "intents": [], "types": [{"name": 7}]}  # a type entry without a string name
    rows = _rows(("uno", "AlphaIntent"))
    result = nlu_selection.select_locale(ES_ES, old, new, rows)
    assert result.payload["status"] == "fallback_all"
    assert "working-tree model unreadable" in result.payload["reason"]
    assert result.payload["rows"] == ["uno"]


def test_select_japan_selects_all_intents_with_an_explicit_note():
    ja = "ja-JP"
    old = _model([_intent("AIntent", ["ひとつ"]), _intent("BIntent", ["ふたつ"])])
    new = _model([_intent("AIntent", ["ひとつ", "みっつ"]), _intent("BIntent", ["ふたつ"])])
    rows = _rows(("ひとつ", "AIntent"), ("ふたつ", "BIntent"))
    result = nlu_selection.select_locale(ja, old, new, rows)
    assert result.payload["status"] == "changed"
    assert result.payload["changed_intents"] == ["AIntent"]
    # All intents are selected: the rest ride the neighbour field and the
    # note says why.
    assert result.payload["neighbour_intents"] == ["BIntent"]
    assert result.payload["rows"] == ["ひとつ", "ふたつ"]  # sorted output
    assert "no sample graph" in result.payload["note"]
    assert "ja-JP" in result.payload["note"]


def test_select_alternation_row_selected_on_either_side():
    rows = _rows(
        ("uno", "AlphaIntent|GammaIntent"),
        ("dos", "BetaIntent|GammaIntent"),
        ("tre", "GammaIntent|DeltaIntent"),
    )
    # Beta changed alone: the Beta|Gamma row is selected through Beta, the
    # Alpha|Gamma row is not.
    only_beta = nlu_selection.select_locale(
        ES_ES,
        _model([_intent("BetaIntent", ["zzz"]), _intent("GammaIntent", ["gamma"])]),
        _model([_intent("BetaIntent", ["dos"]), _intent("GammaIntent", ["gamma"])]),
        rows,
    )
    assert only_beta.payload["changed_intents"] == ["BetaIntent"]
    assert only_beta.payload["rows"] == ["dos"]
    # Alpha changed alone, same rows: the Alpha|Gamma row is selected through
    # Alpha, proving either alternative is enough.
    only_alpha = nlu_selection.select_locale(
        ES_ES,
        _model([_intent("AlphaIntent", ["vecchio"]), _intent("GammaIntent", ["gamma"])]),
        _model([_intent("AlphaIntent", ["uno"]), _intent("GammaIntent", ["gamma"])]),
        rows,
    )
    assert only_alpha.payload["changed_intents"] == ["AlphaIntent"]
    assert only_alpha.payload["rows"] == ["uno"]
    # Gamma and Delta untouched everywhere: that row never selects.
    assert only_alpha.payload["neighbour_intents"] == []


def test_select_alternation_ignores_whitespace_around_alternatives():
    old = _model([_intent("AlphaIntent", ["uno"])])
    new = _model([_intent("AlphaIntent", ["uno", "due"])])
    rows = [{"utterance": "due", "expected_intent": " AlphaIntent |  BetaIntent "}]
    result = nlu_selection.select_locale(ES_ES, old, new, rows)
    assert result.payload["rows"] == ["due"]


def test_select_counts_malformed_rows_and_skips_them_loudly():
    old = _model([_intent("AlphaIntent", ["uno"])])
    new = _model([_intent("AlphaIntent", ["uno", "due"])])
    rows = [
        {"utterance": "due", "expected_intent": "AlphaIntent"},
        {"utterance": "senza intent"},
        {"expected_intent": "AlphaIntent"},
        "not a dict",
    ]
    result = nlu_selection.select_locale(ES_ES, old, new, rows)
    assert result.payload["rows"] == ["due"]
    assert result.malformed == 3
    assert result.rows_total == 4


# ------------------------------------------------- driver and determinism


def _write_synthetic_tree(tmp_path: Path) -> tuple[Path, Path, dict, dict]:
    """Models dir, fixtures dir and the old/new es-ES models for the driver."""
    models_dir = tmp_path / "models"
    fixtures_dir = tmp_path / "fixtures"
    models_dir.mkdir()
    fixtures_dir.mkdir()
    new_model = _model([_intent("AlphaIntent", ["uno", "due"]), _intent("BetaIntent", ["dos"])])
    (models_dir / f"model_{ES_ES}.json").write_text(
        json.dumps(new_model, ensure_ascii=False), encoding="utf-8"
    )
    _write_fixture_yaml(
        fixtures_dir, ES_ES, [("uno", "AlphaIntent"), ("due", "AlphaIntent"), ("dos", "BetaIntent")]
    )
    old_model = _model([_intent("AlphaIntent", ["uno"]), _intent("BetaIntent", ["dos"])])
    return models_dir, fixtures_dir, old_model, new_model


def test_compute_selection_deterministic_and_totals(tmp_path, monkeypatch):
    models_dir, fixtures_dir, old_model, new_model = _write_synthetic_tree(tmp_path)
    monkeypatch.setattr(nlu_selection, "git_show_model", lambda base, locale, repo_root=None: old_model)
    payload1, notes1 = nlu_selection.compute_selection(
        "HEAD~1", [ES_ES], fixtures_dir=fixtures_dir, models_dir=models_dir
    )
    payload2, notes2 = nlu_selection.compute_selection(
        "HEAD~1", [ES_ES], fixtures_dir=fixtures_dir, models_dir=models_dir
    )
    assert notes1 == notes2 == []
    assert payload1 == payload2
    assert json.dumps(payload1, sort_keys=True) == json.dumps(payload2, sort_keys=True)
    entry = payload1["locales"][ES_ES]
    assert entry["status"] == "changed"
    assert entry["changed_intents"] == ["AlphaIntent"]
    assert entry["rows"] == ["due", "uno"]  # sorted output
    assert payload1["totals"] == {"rows_selected": 2, "rows_total": 3}
    assert payload1["schema"] == nlu_selection.SELECTION_SCHEMA
    assert payload1["base"] == "HEAD~1"


def test_compute_selection_two_runs_byte_identical_json(tmp_path, monkeypatch):
    models_dir, fixtures_dir, old_model, _new = _write_synthetic_tree(tmp_path)
    monkeypatch.setattr(nlu_selection, "git_show_model", lambda base, locale, repo_root=None: old_model)
    docs = []
    for _ in range(2):
        payload, _notes = nlu_selection.compute_selection(
            "HEAD~1", [ES_ES], fixtures_dir=fixtures_dir, models_dir=models_dir
        )
        docs.append(json.dumps(payload, indent=2, sort_keys=True) + "\n")
    assert docs[0] == docs[1]


def test_compute_selection_missing_fixture_is_noted_and_totaled_zero(tmp_path, monkeypatch):
    models_dir, fixtures_dir, old_model, _new = _write_synthetic_tree(tmp_path)
    fr = "fr-FR"
    (models_dir / f"model_{fr}.json").write_text(
        json.dumps(_model([_intent("AlphaIntent", ["bonjour"])]), ensure_ascii=False),
        encoding="utf-8",
    )

    def _git(base, locale, repo_root=None):
        if locale == ES_ES:
            return old_model
        raise nlu_selection.ModelMissingAtBase("path does not exist at the ref")

    monkeypatch.setattr(nlu_selection, "git_show_model", _git)
    payload, notes = nlu_selection.compute_selection(
        "HEAD~1", [ES_ES, fr], fixtures_dir=fixtures_dir, models_dir=models_dir
    )
    assert notes == [f"SKIP {fr}: no fixture file"]
    assert payload["locales"][fr]["status"] == "changed"  # missing at base: fully changed
    assert payload["locales"][fr]["rows"] == []
    assert payload["totals"]["rows_total"] == 3  # only es-ES contributes


def test_compute_selection_strict_locale_refuses_missing_fixture(tmp_path, monkeypatch):
    models_dir, fixtures_dir, old_model, _new = _write_synthetic_tree(tmp_path)
    monkeypatch.setattr(nlu_selection, "git_show_model", lambda base, locale, repo_root=None: old_model)
    with pytest.raises(ValueError, match="no fixture file"):
        nlu_selection.compute_selection(
            "HEAD~1",
            ["fr-FR"],
            fixtures_dir=fixtures_dir,
            models_dir=models_dir,
            strict_locale=True,
        )


def test_compute_selection_unreadable_base_falls_back(tmp_path, monkeypatch):
    models_dir, fixtures_dir, _old, new_model = _write_synthetic_tree(tmp_path)

    def _raise(base, locale, repo_root=None):
        raise nlu_selection.GitShowError("fatal: bad object")

    monkeypatch.setattr(nlu_selection, "git_show_model", _raise)
    (models_dir / f"model_{ES_ES}.json").write_text(
        json.dumps(new_model, ensure_ascii=False), encoding="utf-8"
    )
    payload, notes = nlu_selection.compute_selection(
        "HEAD~1", [ES_ES], fixtures_dir=fixtures_dir, models_dir=models_dir
    )
    assert notes == []
    entry = payload["locales"][ES_ES]
    assert entry["status"] == "fallback_all"
    assert "model at base unreadable" in entry["reason"]
    assert len(entry["rows"]) == 3
    assert payload["totals"] == {"rows_selected": 3, "rows_total": 3}


def test_select_cli_byte_identical_against_head(capsys):
    # The working tree's es-ES model equals HEAD's (the toolkit never edits
    # models), so the real CLI run must report unchanged and stay
    # byte-identical across two runs.
    argv = ["select", "--base", "HEAD", "--locale", ES_ES]
    rc = nlu_cli.main(argv)
    out = capsys.readouterr().out
    nlu_cli.main(argv)
    out_again = capsys.readouterr().out
    assert rc == 0
    assert out == out_again
    payload = json.loads(out)
    assert payload["schema"] == nlu_selection.SELECTION_SCHEMA
    assert payload["base"] == "HEAD"
    assert payload["locales"][ES_ES] == {"status": "unchanged"}
    assert payload["totals"]["rows_selected"] == 0
    assert payload["totals"]["rows_total"] > 0


def test_select_cli_text_mode_is_deterministic(tmp_path, capsys):
    text_path = tmp_path / "rows.txt"
    rc1 = nlu_cli.main(["select", "--base", "HEAD", "--locale", ES_ES, "--format", "text"])
    capsys.readouterr()
    rc2 = nlu_cli.main(
        ["select", "--base", "HEAD", "--locale", ES_ES, "--format", "text", "--out", str(text_path)]
    )
    capsys.readouterr()
    assert rc1 == rc2 == 0
    rendered = text_path.read_text(encoding="utf-8")
    assert f"  {ES_ES}: unchanged" in rendered
    assert rendered.startswith("select base=HEAD neighbours=on\n")
    assert "totals: rows_selected=0" in rendered


def test_select_cli_refuses_unresolvable_base(capsys):
    rc = nlu_cli.main(["select", "--base", "no-such-ref-9547", "--locale", ES_ES])
    captured = capsys.readouterr()
    assert rc == 2
    assert "cannot resolve ref" in captured.err


def test_verify_git_ref_accepts_head():
    nlu_selection.verify_git_ref("HEAD")  # must not raise


# ------------------------------------------------- Part C: the filter


def _payload_with_rows(locales: dict) -> dict:
    return {
        "schema": nlu_selection.SELECTION_SCHEMA,
        "base": "HEAD~1",
        "locales": locales,
        "totals": {"rows_selected": 1, "rows_total": 2},
    }


def test_row_filter_index_and_should_keep_match_normalized():
    doc = _payload_with_rows(
        {
            ES_ES: {
                "status": "changed",
                "changed_intents": ["AlphaIntent"],
                "neighbour_intents": [],
                "rows": ["Reproduce Adolescencia", "Pon Café"],
            },
            "fr-FR": {"status": "unchanged"},
        }
    )
    row_filter = nlu_selection.row_filter_from_payload(doc)
    assert nlu_selection.should_keep(ES_ES, "reproduce adolescencia", row_filter)
    assert nlu_selection.should_keep(ES_ES, "Reproduce  adolescencia", row_filter)
    assert nlu_selection.should_keep(ES_ES, "pon café", row_filter)
    assert nlu_selection.should_keep(ES_ES, "pon café", row_filter)  # NFC composition
    assert not nlu_selection.should_keep(ES_ES, "otra cosa", row_filter)
    assert not nlu_selection.should_keep("fr-FR", "Reproduce Adolescencia", row_filter)  # unchanged
    assert not nlu_selection.should_keep("de-DE", "Reproduce Adolescencia", row_filter)  # absent


def test_row_filter_loads_from_file(tmp_path):
    path = tmp_path / "rows.json"
    path.write_text(json.dumps(_payload_with_rows(
        {ES_ES: {"status": "changed", "changed_intents": [], "neighbour_intents": [], "rows": ["uno"]}}
    )), encoding="utf-8")
    row_filter = nlu_selection.load_row_filter(path)
    assert nlu_selection.should_keep(ES_ES, "uno", row_filter)
    assert not nlu_selection.should_keep(ES_ES, "dos", row_filter)


@pytest.mark.parametrize(
    "bad,why",
    [
        ("{oops", "not valid JSON"),
        ("[]", "top level must be a JSON object"),
        ('{"schema": 2, "locales": {}}', "schema must be 1"),
        ('{"schema": true, "locales": {}}', "schema must be 1"),
        ('{"schema": 1}', "must be an object keyed by locale"),
        ('{"schema": 1, "locales": {"es-ES": {"status": "changed"}}}', "must be a list of strings"),
        (
            '{"schema": 1, "locales": {"es-ES": {"status": "changed", "rows": [1]}}}',
            "must be a list of strings",
        ),
        (
            '{"schema": 1, "locales": {"es-ES": {"status": "changed", "rows": ["x"], "extra": {"nested": true}}}}',
            None,
        ),
    ],
)
def test_row_filter_rejects_bad_files(tmp_path, bad, why):
    path = tmp_path / "rows.json"
    path.write_text(bad, encoding="utf-8")
    if why is None:
        # Extra unknown keys are tolerated: the schema check pins what the
        # suite needs (schema, locales, status, rows) and nothing else.
        row_filter = nlu_selection.load_row_filter(path)
        assert nlu_selection.should_keep(ES_ES, "x", row_filter)
        return
    with pytest.raises(ValueError, match=why):
        nlu_selection.load_row_filter(path)


def test_row_filter_rejects_non_json_object_and_missing_file(tmp_path):
    path = tmp_path / "rows.json"
    path.write_text('"a string"', encoding="utf-8")
    with pytest.raises(ValueError):
        nlu_selection.load_row_filter(path)
    with pytest.raises(OSError):
        nlu_selection.load_row_filter(tmp_path / "nope.json")


# ------------------------------------------ the conftest hook (integration)


def _load_integration_conftest():
    """The integration conftest module, loaded fresh for hook-level tests."""
    path = REPO / "tests" / "integration" / "conftest.py"
    spec = importlib.util.spec_from_file_location("jf9547_integration_conftest", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def _fake_config(deselected: list) -> SimpleNamespace:
    return SimpleNamespace(
        getoption=lambda name, default=False: False,
        hook=SimpleNamespace(pytest_deselected=lambda items: deselected.extend(items)),
    )


def _fake_nlu_item(locale: str, utterance: str) -> SimpleNamespace:
    return SimpleNamespace(
        callspec=SimpleNamespace(
            params={"nlu_fixture": {"locale": locale, "utterance": utterance}},
            id=f"case:{locale}:{utterance}",
        ),
        add_marker=lambda *args, **kwargs: None,
    )


def _fake_e2e_item(locale: str, utterance: str) -> SimpleNamespace:
    return SimpleNamespace(
        callspec=SimpleNamespace(
            params={"e2e_fixture": {"locale": locale, "utterance": utterance}},
            id=f"e2e-case:{locale}:{utterance}",
        ),
        add_marker=lambda *args, **kwargs: None,
    )


def test_conftest_filter_deselects_unlisted_and_spares_e2e(tmp_path, monkeypatch, capsys):
    conftest = _load_integration_conftest()
    rows_path = tmp_path / "rows.json"
    rows_path.write_text(
        json.dumps(_payload_with_rows(
            {ES_ES: {"status": "changed", "changed_intents": [], "neighbour_intents": [],
                     "rows": ["keep me"]}}
        )),
        encoding="utf-8",
    )
    monkeypatch.setenv("NLU_ROW_FILTER", str(rows_path))
    keep = _fake_nlu_item(ES_ES, "keep me")
    drop = _fake_nlu_item(ES_ES, "drop me")
    other_locale = _fake_nlu_item("fr-FR", "keep me")  # same text, other locale
    e2e = _fake_e2e_item(ES_ES, "keep me")  # non-NLU family: never touched
    items = [keep, drop, other_locale, e2e]
    deselected: list = []
    conftest.pytest_collection_modifyitems(_fake_config(deselected), items)
    assert items == [keep, e2e]
    assert deselected == [drop, other_locale]
    out = capsys.readouterr().out
    assert "[nlu_row_filter] kept 1 of 3 NLU rows" in out
    assert str(rows_path) in out
    assert "2 deselected" in out


def test_conftest_filter_keeps_normalized_matches(tmp_path, monkeypatch, capsys):
    conftest = _load_integration_conftest()
    rows_path = tmp_path / "rows.json"
    rows_path.write_text(
        json.dumps(_payload_with_rows(
            {ES_ES: {"status": "changed", "changed_intents": [], "neighbour_intents": [],
                     "rows": ["Reproduce Adolescencia"]}}
        )),
        encoding="utf-8",
    )
    monkeypatch.setenv("NLU_ROW_FILTER", str(rows_path))
    items = [_fake_nlu_item(ES_ES, "reproduce  adolescencia")]
    conftest.pytest_collection_modifyitems(_fake_config([]), items)
    assert len(items) == 1
    capsys.readouterr()


def test_conftest_filter_bad_file_aborts(tmp_path, monkeypatch):
    conftest = _load_integration_conftest()
    bad = tmp_path / "rows.json"
    bad.write_text("{oops", encoding="utf-8")
    monkeypatch.setenv("NLU_ROW_FILTER", str(bad))
    with pytest.raises(PytestExit):
        conftest.pytest_collection_modifyitems(_fake_config([]), [_fake_nlu_item(ES_ES, "x")])


def test_conftest_filter_missing_file_aborts(tmp_path, monkeypatch):
    conftest = _load_integration_conftest()
    monkeypatch.setenv("NLU_ROW_FILTER", str(tmp_path / "nope.json"))
    with pytest.raises(PytestExit):
        conftest.pytest_collection_modifyitems(_fake_config([]), [])


def test_conftest_filter_unset_is_a_noop(monkeypatch, capsys):
    conftest = _load_integration_conftest()
    monkeypatch.delenv("NLU_ROW_FILTER", raising=False)
    first = _fake_nlu_item(ES_ES, "x")
    second = _fake_e2e_item(ES_ES, "y")
    items = [first, second]
    deselected: list = []
    conftest.pytest_collection_modifyitems(_fake_config(deselected), items)
    assert items == [first, second]
    assert deselected == []
    assert "[nlu_row_filter]" not in capsys.readouterr().out


def test_normalize_helper_is_the_shared_one():
    # Part C's matching must ride the ONE normalizer, not a second copy.
    assert normalize_text("Pon  Café") == "pon café"
