"""cmd_export and cmd_compare, both fully offline (no lab.json, no network, no
deploy). export must never write a payload whose invocationName is not the
caller's explicit --invocation-name: the plugin's custom-model import deploys
the file's invocationName verbatim, so a file carrying the source model's or
the lab's name would rename the importing skill (the rename hazard). compare
diffs two run files; --expect-zero-changes is the A/A gate (exit 1 on any
non-same row)."""
import json
from pathlib import Path

import pytest

import _paths
_paths.bootstrap()

import cli
import models
from runs import RowResult, RowStatus, RunRecord, save_run

SOURCE_MODEL = {
    "languageModel": {
        # the source's own name must NEVER survive into the exported file
        "invocationName": "meine sammlung",
        "intents": [
            {"name": "PlaySongIntent", "samples": ["spiele das lied x", "spiele das lied y"]},
            {"name": "AMAZON.StopIntent", "samples": []},
            {"name": "ZeroSamplesIntent"},  # validator WARNING, never a blocker
        ],
    }
}


def _model_file(tmp_path, model=SOURCE_MODEL):
    p = tmp_path / "model.json"
    p.write_text(json.dumps(model), encoding="utf-8")
    return p


def _export_args(model_path, out, *extra, label="jf933 lab", name="mia collezione"):
    argv = ["export", "--locale", "de-DE", "--model", str(model_path),
            "--label", label, "--invocation-name", name, "--out", str(out), *extra]
    return cli.build_parser().parse_args(argv)


# --- export ---

def test_export_writes_payload_with_the_given_invocation_name(tmp_path, capsys):
    # no lab.json anywhere in tmp_path: export never consults lab config, so
    # the lab's invocation name cannot leak into the file either
    out = tmp_path / "out"  # does not exist yet; export must create it
    rc = cli.cmd_export(_export_args(_model_file(tmp_path), out))
    assert rc == 0
    path = out / "de-DE-jf933-lab.json"
    payload = json.loads(path.read_text(encoding="utf-8"))
    assert "interactionModel" in payload
    lm = payload["interactionModel"]["languageModel"]
    assert lm["invocationName"] == "mia collezione"
    assert lm["invocationName"] != SOURCE_MODEL["languageModel"]["invocationName"]
    assert [i["name"] for i in lm["intents"]] == [
        "PlaySongIntent", "AMAZON.StopIntent", "ZeroSamplesIntent"]
    assert lm["intents"][0]["samples"] == ["spiele das lied x", "spiele das lied y"]
    captured = capsys.readouterr()
    assert str(path) in captured.out
    assert "warning" in captured.err.lower()  # ZeroSamplesIntent warned, did not block


def test_export_invocation_name_is_required_by_the_parser(tmp_path):
    with pytest.raises(SystemExit) as ei:
        cli.build_parser().parse_args(
            ["export", "--locale", "de-DE", "--model", str(_model_file(tmp_path)),
             "--label", "l"])
    assert ei.value.code == 2


def test_export_refuses_blank_invocation_name_and_writes_nothing(tmp_path, capsys):
    out = tmp_path / "out"
    rc = cli.cmd_export(_export_args(_model_file(tmp_path), out, name="   "))
    assert rc == 1
    assert not out.exists()
    assert "invocation" in capsys.readouterr().err.lower()


def test_export_template_source_builds_in_memory(tmp_path, monkeypatch):
    built = []

    def fake_build(locale):
        built.append(locale)
        return SOURCE_MODEL
    monkeypatch.setattr(models, "build_from_template", fake_build)
    args = cli.build_parser().parse_args(
        ["export", "--locale", "de-DE", "--template", "--label", "tpl",
         "--invocation-name", "laboratorio", "--out", str(tmp_path / "out")])
    assert cli.cmd_export(args) == 0
    assert built == ["de-DE"]
    payload = json.loads((tmp_path / "out" / "de-DE-tpl.json").read_text(encoding="utf-8"))
    assert payload["interactionModel"]["languageModel"]["invocationName"] == "laboratorio"


def test_export_preflight_refuses_structural_errors(tmp_path, capsys):
    broken = {"languageModel": {"invocationName": "meine sammlung", "intents": []}}
    out = tmp_path / "out"
    rc = cli.cmd_export(_export_args(_model_file(tmp_path, broken), out))
    assert rc == 1
    assert not out.exists()
    assert "intents" in capsys.readouterr().err


def test_export_refuses_nonexistent_model_path(tmp_path, capsys):
    out = tmp_path / "out"
    rc = cli.cmd_export(_export_args(tmp_path / "nope.json", out))
    assert rc == 1
    assert not out.exists()
    assert "no such file" in capsys.readouterr().err


def test_export_defaults_out_to_cwd(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    args = cli.build_parser().parse_args(
        ["export", "--locale", "de-DE", "--model", str(_model_file(tmp_path)),
         "--label", "x", "--invocation-name", "mia collezione"])
    assert cli.cmd_export(args) == 0
    assert (tmp_path / "de-DE-x.json").is_file()


# --- compare ---

def _row(utterance, selected, expected="PlaySongIntent", source="fixture"):
    return RowResult(utterance=utterance, source=source, status=RowStatus.OK,
                     expected_intent=expected, selected_intent=selected,
                     slots={}, considered_intents=[], raw={})


RUN_A_ROWS = [
    _row("spiele das lied x", "PlaySongIntent"),           # stays same
    _row("wiederholung aus", "PlaySongIntent"),            # changes, violates -> red
    _row("beobachtung", "MediaInfoIntent", expected=None),  # changes, no expectation -> amber
    _row("nur im alten run", "PlaySongIntent"),            # absent_old
]
RUN_B_ROWS = [
    _row("spiele das lied x", "PlaySongIntent"),
    _row("wiederholung aus", "LoopAllOffIntent"),
    _row("beobachtung", "PlaySongIntent", expected=None),
    _row("nur im neuen run", "PlaySongIntent"),            # absent_new
]


def _save_run(lab_dir, run_id, results, locale="de-DE", sha="0" * 64):
    from runs import RunRecord
    run = RunRecord(id=run_id, timestamp="2026-11-21T00:00:00", locale=locale,
                    label=run_id, model_source="m", model_sha256=sha,
                    skill_id="s", status="ok", intent_roster=[], results=results)
    save_run(lab_dir / "runs", run)


def test_compare_prints_changed_rows_and_summary(tmp_path, capsys):
    _save_run(tmp_path, "run-a", RUN_A_ROWS)
    _save_run(tmp_path, "run-b", RUN_B_ROWS)
    args = cli.build_parser().parse_args(["compare", "run-a", "run-b"])
    assert cli.cmd_compare(args, lab_dir=tmp_path) == 0
    out = capsys.readouterr().out
    assert "red  wiederholung aus  (fixture)" in out
    assert "amber  beobachtung  (fixture)" in out
    assert "absent_old  nur im alten run  (fixture)" in out
    assert "absent_new  nur im neuen run  (fixture)" in out
    assert "4 changed of 5 rows" in out
    assert "spiele das lied x" not in out  # same rows are not printed


def test_compare_aa_gate_zero_changes_passes(tmp_path, capsys):
    _save_run(tmp_path, "run-a", RUN_A_ROWS)
    args = cli.build_parser().parse_args(
        ["compare", "--lab-dir", str(tmp_path), "run-a", "run-a",
         "--expect-zero-changes"])
    assert cli.cmd_compare(args) == 0  # --lab-dir flag resolution, no kwarg
    assert "0 changed of 4 rows" in capsys.readouterr().out


def test_compare_expect_zero_changes_exits_nonzero(tmp_path, capsys):
    _save_run(tmp_path, "run-a", RUN_A_ROWS)
    _save_run(tmp_path, "run-b", RUN_B_ROWS)
    args = cli.build_parser().parse_args(
        ["compare", "run-a", "run-b", "--expect-zero-changes"])
    assert cli.cmd_compare(args, lab_dir=tmp_path) == 1
    assert "4 changed of 5 rows" in capsys.readouterr().out


def test_compare_warns_loudly_on_locale_mismatch(tmp_path, capsys):
    _save_run(tmp_path, "run-a", RUN_A_ROWS, locale="de-DE")
    _save_run(tmp_path, "run-b", RUN_B_ROWS, locale="it-IT")
    args = cli.build_parser().parse_args(["compare", "run-a", "run-b"])
    assert cli.cmd_compare(args, lab_dir=tmp_path) == 0
    err = capsys.readouterr().err
    assert "WARNING" in err and "de-DE" in err and "it-IT" in err


def test_compare_refuses_unknown_run_id(tmp_path, capsys):
    _save_run(tmp_path, "run-a", RUN_A_ROWS)
    args = cli.build_parser().parse_args(["compare", "run-a", "nope"])
    assert cli.cmd_compare(args, lab_dir=tmp_path) == 1
    err = capsys.readouterr().err
    assert "not found" in err and str(tmp_path / "runs" / "nope.json") in err


def test_compare_refuses_malformed_run_file_without_traceback(tmp_path, capsys):
    # valid JSON, wrong shape: load_run's dict accesses would raise
    # AttributeError/TypeError, which must surface as a refusal, never a traceback
    (tmp_path / "runs").mkdir()
    (tmp_path / "runs" / "bad.json").write_text("[1, 2, 3]", encoding="utf-8")
    args = cli.build_parser().parse_args(["compare", "bad", "bad"])
    assert cli.cmd_compare(args, lab_dir=tmp_path) == 1
    err = capsys.readouterr().err
    assert "unreadable" in err and "bad.json" in err


def test_compare_sha_mismatch_fails_the_aa_gate_and_warns_otherwise(tmp_path, capsys):
    """The A/A integrity guard: two runs that probed DIFFERENT models cannot
    certify zero-change noise; without the gate it is a loud warning."""
    row = RowResult(utterance="u", source="fixture", status=RowStatus.OK,
                             expected_intent="X", selected_intent="X", slots={},
                             considered_intents=["X"], raw={})
    _save_run(tmp_path, "aaA", [row], sha="a" * 64)
    _save_run(tmp_path, "aaB", [row], sha="b" * 64)
    args_gate = cli.build_parser().parse_args(["compare", "aaA", "aaB", "--expect-zero-changes"])
    rc = cli.cmd_compare(args_gate, lab_dir=tmp_path)
    assert rc == 1 and "sha mismatch" in capsys.readouterr().err
    args_plain = cli.build_parser().parse_args(["compare", "aaA", "aaB"])
    rc = cli.cmd_compare(args_plain, lab_dir=tmp_path)
    assert rc == 0 and "WARNING" in capsys.readouterr().err


def test_compare_etag_mismatch_fails_the_aa_gate(tmp_path, capsys):
    """Same file, different BUILDS (the skip-deploy false-pass direction):
    the eTag chain must refuse the gate even when the shas match."""
    row = RowResult(utterance="u", source="fixture", status=RowStatus.OK,
                             expected_intent="X", selected_intent="X", slots={},
                             considered_intents=["X"], raw={})
    (tmp_path / "runs").mkdir()
    for rid, etag in (("r1", "E-1"), ("r2", "E-2")):
        rec = RunRecord(id=rid, timestamp="t", locale="de-DE", label=rid,
                                 model_source="m", model_sha256="a" * 64, skill_id="s",
                                 status="ok", intent_roster=[], results=[row], build_etag=etag)
        save_run(tmp_path / "runs", rec)
    args = cli.build_parser().parse_args(["compare", "r1", "r2", "--expect-zero-changes"])
    assert cli.cmd_compare(args, lab_dir=tmp_path) == 1
    assert "different builds" in capsys.readouterr().err
