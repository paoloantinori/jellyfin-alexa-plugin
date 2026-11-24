import yaml
import pytest

from battery import WorkingsetRefused, load_battery

FIXTURE_TESTS = [
    {"utterance": "Wiederholung aus", "expected_intent": "LoopAllOffIntent", "expected_slots": {}},
    {"utterance": "Lied wiederholen", "expected_intent": "RepeatSingleOnIntent|AMAZON.RepeatIntent", "expected_slots": {}},
]


def _doc(locale="de-DE", tests=()):
    return {"locale": locale, "invocation_name": "meine sammlung", "tests": list(tests)}


def _write(tmp_path, name, doc):
    p = tmp_path / name
    p.write_text(yaml.safe_dump(doc, allow_unicode=True), encoding="utf-8")
    return p


def test_fixture_load_excludes_e2e_family(tmp_path):
    _write(tmp_path, "de-DE.yaml", _doc(tests=FIXTURE_TESTS))
    _write(tmp_path, "e2e_smoke_de-DE.yaml", _doc(tests=[{"utterance": "e2e only row", "expected_intent": "SomeIntent"}]))
    rows = load_battery("de-DE", tmp_path, None, None)
    assert [r.utterance for r in rows] == ["Wiederholung aus", "Lied wiederholen"]
    assert all(r.source == "fixture" for r in rows)


def test_workingset_missing_file_refused(tmp_path):
    """A provided-but-missing workingset refuses (never silently contributes
    zero rows); the CLI-facing '--workingset ...: no such file' text is the
    same message the old cli-side existence check emitted."""
    with pytest.raises(WorkingsetRefused, match="no such file"):
        load_battery("de-DE", tmp_path, tmp_path / "nope.yaml", None)


def test_workingset_locale_mismatch_refused(tmp_path):
    """Moved from test_cli_run (the refusal now lives here, where the workingset
    is parsed once): a workingset declaring another locale must refuse with the
    CLI-facing message text, never silently feed foreign rows to profile-nlu."""
    ws = _write(tmp_path, "ws.yaml", _doc(locale="it-IT",
                                          tests=[{"utterance": "riproduci"}]))
    with pytest.raises(WorkingsetRefused, match="it-IT.*de-DE"):
        load_battery("de-DE", tmp_path, ws, None)


def test_workingset_unparseable_yaml_refused(tmp_path):
    ws = tmp_path / "ws.yaml"
    ws.write_text("tests: [unclosed", encoding="utf-8")
    with pytest.raises(WorkingsetRefused, match="not parseable YAML"):
        load_battery("de-DE", tmp_path, ws, None)


def test_workingset_non_mapping_top_level_refused(tmp_path):
    ws = _write(tmp_path, "ws.yaml", ["not", "a", "mapping"])
    with pytest.raises(WorkingsetRefused, match="expected a mapping"):
        load_battery("de-DE", tmp_path, ws, None)


def test_fixture_row_with_typo_key_refuses(tmp_path):
    """JF-935: a fixture row keyed 'utterrence' used to raise KeyError, which
    escaped the CLI's except (RunRefused, LabConfigError) as a raw traceback.
    The refusal names the file and the 1-based row index, and lists the row's
    keys so the typo is visible."""
    _write(tmp_path, "de-DE.yaml", _doc(tests=[
        {"utterance": "ok row", "expected_intent": "A"},
        {"utterrence": "typo row", "expected_intent": "B"}]))
    with pytest.raises(WorkingsetRefused) as exc_info:
        load_battery("de-DE", tmp_path, None, None)
    msg = str(exc_info.value)
    assert "row #2" in msg and "de-DE.yaml" in msg
    assert "no utterance" in msg and "'utterrence'" in msg


def test_fixture_row_bare_list_item_refuses(tmp_path):
    """JF-935: '- some utterance' (a bare string row) used to raise TypeError
    on case.get; it refuses with the file and row index instead."""
    _write(tmp_path, "de-DE.yaml", _doc(tests=["some utterance"]))
    with pytest.raises(WorkingsetRefused, match=r"row #1.*expected a mapping"):
        load_battery("de-DE", tmp_path, None, None)


def test_workingset_row_with_typo_key_refuses(tmp_path):
    """JF-935: the same typo'd-key shape in a workingset row refused the same
    way (KeyError before the fix), naming the workingset file and row index."""
    ws = _write(tmp_path, "ws.yaml", _doc(tests=[{"utterrence": "spiele musik"}]))
    with pytest.raises(WorkingsetRefused) as exc_info:
        load_battery("de-DE", tmp_path, ws, None)
    msg = str(exc_info.value)
    assert "workingset" in msg and "ws.yaml" in msg and "row #1" in msg
    assert "no utterance" in msg


def test_workingset_row_bare_list_item_refuses(tmp_path):
    ws = _write(tmp_path, "ws.yaml", _doc(tests=["spiele musik"]))
    with pytest.raises(WorkingsetRefused, match=r"row #1.*expected a mapping"):
        load_battery("de-DE", tmp_path, ws, None)


def test_null_utterance_still_reaches_cli_refusal(tmp_path):
    """JF-935 scope guard: an utterance key PRESENT with a None value is not
    battery's refusal; it must keep flowing to the CLI's None/empty check
    unchanged (test_cli_run.test_run_refuses_null_utterance_row owns it)."""
    ws = _write(tmp_path, "ws.yaml", _doc(tests=[{"utterance": None}]))
    rows = load_battery("de-DE", tmp_path, ws, None)
    assert len(rows) == 1 and rows[0].utterance is None


def test_null_utterance_bypasses_filter_k(tmp_path):
    """JF-935 review fix: with -k set the bare membership test TypeError'd on
    a None-utterance row before the CLI's refusal could see it; non-string
    utterances now bypass the filter so the row still reaches that refusal."""
    ws = _write(tmp_path, "ws.yaml", _doc(tests=[
        {"utterance": "spiele musik", "expected_intent": "A"},
        {"utterance": None}]))
    rows = load_battery("de-DE", tmp_path, ws, "kein-treffer")
    assert [r.utterance for r in rows] == [None]
    # a matching filter keeps BOTH rows: strings filter, the None row is kept
    # for the CLI's refusal instead of being silently dropped or crashing
    rows = load_battery("de-DE", tmp_path, ws, "spiele")
    assert [r.utterance for r in rows] == ["spiele musik", None]


def test_workingset_loads_fixture_format(tmp_path):
    fixtures = tmp_path / "fixtures"
    fixtures.mkdir()
    ws = _write(tmp_path, "workingset.yaml", _doc(tests=[
        {"utterance": "spiele das album magellan", "expected_intent": "PlayAlbumIntent"}]))
    rows = load_battery("de-DE", fixtures, ws, None)
    assert [(r.utterance, r.source, r.expected_intent) for r in rows] == [
        ("spiele das album magellan", "workingset", "PlayAlbumIntent")]


def test_missing_expected_intent_is_none(tmp_path):
    fixtures = tmp_path / "fixtures"
    fixtures.mkdir()
    ws = _write(tmp_path, "ws.yaml", _doc(tests=[{"utterance": "spiele die streithörnchen"}]))
    rows = load_battery("de-DE", fixtures, ws, None)
    assert len(rows) == 1
    assert rows[0].expected_intent is None


def test_pipe_alternatives_preserved_verbatim(tmp_path):
    ws = _write(tmp_path, "ws.yaml", _doc(tests=FIXTURE_TESTS))
    rows = load_battery("de-DE", tmp_path, ws, None)
    assert rows[1].expected_intent == "RepeatSingleOnIntent|AMAZON.RepeatIntent"


def test_filter_k_applies_to_both_sources(tmp_path):
    fixtures = tmp_path / "fixtures"
    fixtures.mkdir()
    _write(fixtures, "de-DE.yaml", _doc(tests=[
        {"utterance": "filter alpha one", "expected_intent": "A"},
        {"utterance": "other two", "expected_intent": "B"}]))
    ws = _write(tmp_path, "ws.yaml", _doc(tests=[
        {"utterance": "filter alpha three", "expected_intent": "C"},
        {"utterance": "other four"}]))
    rows = load_battery("de-DE", fixtures, ws, "alpha")
    assert {(r.utterance, r.source) for r in rows} == {
        ("filter alpha one", "fixture"), ("filter alpha three", "workingset")}
    assert load_battery("de-DE", fixtures, ws, "zzz-no-match") == []


def test_duplicate_utterance_across_sources_distinct(tmp_path):
    fixtures = tmp_path / "fixtures"
    fixtures.mkdir()
    _write(fixtures, "de-DE.yaml", _doc(tests=[{"utterance": "spiele musik von die streithörnchen", "expected_intent": "FixtureIntent"}]))
    ws = _write(tmp_path, "ws.yaml", _doc(tests=[{"utterance": "spiele musik von die streithörnchen", "expected_intent": "WorkingsetIntent"}]))
    rows = load_battery("de-DE", fixtures, ws, None)
    assert len(rows) == 2
    by_source = {r.source: r for r in rows}
    assert by_source["fixture"].expected_intent == "FixtureIntent"
    assert by_source["workingset"].expected_intent == "WorkingsetIntent"
