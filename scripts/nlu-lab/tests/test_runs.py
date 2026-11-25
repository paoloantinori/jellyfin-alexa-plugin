import json
import pytest
from runs import RunLoadError, RunRecord, RowResult, RowStatus, load_run, save_run

def make_row(**kw):
    base = dict(utterance="u", source="fixture", status=RowStatus.OK, expected_intent="PlaySongIntent",
                selected_intent="PlaySongIntent", slots={}, considered_intents=["PlaySongIntent"], raw={})
    base.update(kw)
    return RowResult(**base)

def test_roundtrip_and_nullability(tmp_path):
    run = RunRecord(id="r1", timestamp="t", locale="de-DE", label="base", model_source="stock",
                    model_sha256="ab" * 32, skill_id="s", status="ok",
                    intent_roster=[{"intent": "PlaySongIntent", "sample": "spiele das lied x"}],
                    results=[make_row(), make_row(utterance="w", expected_intent=None, status=RowStatus.NO_SELECTION, selected_intent=None)])
    p = save_run(tmp_path, run)
    back = load_run(p)
    assert back.results[0].expected_intent == "PlaySongIntent"
    assert back.results[1].expected_intent is None
    assert back.results[1].status is RowStatus.NO_SELECTION

def test_failed_and_interrupted_roundtrip(tmp_path):
    for status in ("failed", "interrupted"):
        run = RunRecord(id="r2", timestamp="t", locale="de-DE", label="x", model_source="m",
                        model_sha256="0" * 64, skill_id="s", status=status, error="boom",
                        intent_roster=[], results=[])
        assert load_run(save_run(tmp_path, run)).status == status

def test_load_run_raises_runloaderror_on_any_malformed_file(tmp_path):
    # the corrupt-run guard lives in load_run itself (Task 9 review note) so every
    # caller (cli compare, the webapp run list) catches one typed exception
    bad = tmp_path / "bad.json"
    for content in ("[1, 2, 3]",                 # valid JSON, not a run dict
                    "{}",                         # missing every required key
                    "not json at all",            # unparseable
                    '{"id": "x", "timestamp": "t", "locale": "l", "label": "l", '
                    '"model_source": "m", "model_sha256": "0", "skill_id": "s", '
                    '"status": "ok", "results": [{"utterance": "u", "source": "f", '
                    '"status": "nonsense"}]}'):    # unknown RowStatus value
        bad.write_text(content, encoding="utf-8")
        with pytest.raises(RunLoadError):
            load_run(bad)
    bad.unlink()
    with pytest.raises(RunLoadError):
        load_run(bad)  # missing file is also a RunLoadError, not a naked OSError

def test_diff_judgment_table(tmp_path):
    def rec(rid, rows):
        return RunRecord(id=rid, timestamp="t", locale="l", label=rid, model_source="m",
                         model_sha256="0"*64, skill_id="s", status="ok", intent_roster=[], results=rows)
    old = rec("a", [make_row(utterance="to-green", selected_intent="PlayRadioIntent"),
                    make_row(utterance="to-red", selected_intent="MediaInfoIntent"),
                    make_row(utterance="noexp", expected_intent=None, selected_intent="MediaInfoIntent"),
                    make_row(utterance="pipe", expected_intent="LoopSongOnIntent|AMAZON.LoopOnIntent", selected_intent="MediaInfoIntent"),
                    make_row(utterance="err-old", selected_intent="X"),
                    make_row(utterance="gone", selected_intent="X")])
    new = rec("b", [make_row(utterance="to-green", selected_intent="PlaySongIntent"),
                    make_row(utterance="to-red", selected_intent="PlayRadioIntent"),
                    make_row(utterance="noexp", expected_intent=None, selected_intent="PlaySongIntent"),
                    make_row(utterance="pipe", expected_intent="LoopSongOnIntent|AMAZON.LoopOnIntent", selected_intent="AMAZON.LoopOnIntent"),
                    make_row(utterance="err-new", status=RowStatus.ERROR, selected_intent=None, expected_intent=None),
                    make_row(utterance="added", selected_intent="Y")])
    diff = {d.utterance: d for d in new.diff_against(old)}
    assert diff["to-green"].kind == "green"    # changed and now satisfies the snapshot expectation
    assert diff["to-red"].kind == "red"        # changed and now violates the snapshot expectation
    assert diff["noexp"].kind == "amber"       # changed with no expectation to judge against
    assert diff["pipe"].kind == "green"        # pipe alternatives: membership satisfies (de-DE fixtures carry these)
    assert diff["err-new"].kind == "error"     # error rows are excluded from judgment
    assert diff["gone"].kind == "absent_old"
    assert diff["added"].kind == "absent_new"

def test_failure_count_pipe_and_no_selection():
    run = RunRecord(id="c", timestamp="t", locale="l", label="c", model_source="m", model_sha256="0"*64,
                    skill_id="s", status="ok", intent_roster=[], results=[
                        make_row(utterance="ok-pipe", expected_intent="A|B", selected_intent="B"),
                        make_row(utterance="fail-pipe", expected_intent="A|B", selected_intent="C"),
                        make_row(utterance="noselection", status=RowStatus.NO_SELECTION, selected_intent=None),
                        make_row(utterance="error", status=RowStatus.ERROR, selected_intent=None, expected_intent=None)])
    assert run.expectation_failure_count == 2  # fail-pipe and no_selection (only ERROR rows are excluded)


# --- JF-968: considered_hypotheses field (write + backward-compatible load) ---

def test_considered_hypotheses_roundtrip(tmp_path):
    hyps = [{"intent": "PlayByGenreIntent", "slots": {"genre": "some jazz"},
             "er": {"genre": [{"authority": "amzn1.er-authority..GENRE",
                               "code": "ER_SUCCESS_MATCH", "canonical": "jazz"}]}},
            {"intent": "PlayByGenreIntent", "slots": {"genre": "jazz"},
             "er": {"genre": [{"authority": "amzn1.er-authority..GENRE",
                               "code": "ER_SUCCESS_NO_MATCH"}]}}]
    run = RunRecord(id="hyp", timestamp="t", locale="it-IT", label="x",
                    model_source="m", model_sha256="0" * 64, skill_id="s",
                    status="ok", intent_roster=[],
                    results=[make_row(considered_hypotheses=hyps)])
    back = load_run(save_run(tmp_path, run))
    assert back.results[0].considered_hypotheses == hyps
    # the JSON file itself carries the field
    saved = json.loads((tmp_path / "hyp.json").read_text())
    assert saved["results"][0]["considered_hypotheses"] == hyps


def test_old_run_file_without_considered_hypotheses_loads(tmp_path):
    # a pre-JF-968 run file: no considered_hypotheses key anywhere
    old = {"id": "old", "timestamp": "t", "locale": "it-IT", "label": "l",
           "model_source": "m", "model_sha256": "0" * 64, "skill_id": "s",
           "status": "ok",
           "results": [{"utterance": "u", "source": "f", "status": "ok",
                        "expected_intent": "PlaySongIntent",
                        "selected_intent": "PlaySongIntent",
                        "slots": {}, "considered_intents": ["PlaySongIntent"],
                        "raw": {}}]}
    p = tmp_path / "old.json"
    p.write_text(json.dumps(old), encoding="utf-8")
    back = load_run(p)
    assert back.results[0].considered_hypotheses == []
    assert back.results[0].considered_intents == ["PlaySongIntent"]
