"""cmd_run orchestration: smapi_ops and models are monkeypatched (no network); battery
assembly, model validation, and the run schema are the REAL modules. Refusal paths
assert deploy never fired and no run file was created; the live paths assert the run
file lifecycle (running -> ok/failed/interrupted) and the lab invocation name in the
deployed payload."""
import json
import subprocess
import sys
from pathlib import Path

import yaml

import _paths
_paths.bootstrap()

import cli
import models
import smapi_ops
from lab_config import LabConfig
from runs import load_run
from smapi_client import SmapiError

SKILL_ID = "amzn1.ask.skill.lab"
LAB_NAME = "jellyfin nlu lab"
LAB_INVOCATION = "nlu labor"
CLI_PATH = Path(__file__).resolve().parents[1] / "cli.py"

FX_ROW_A = {"utterance": "spiele das lied x", "expected_intent": "PlaySongIntent"}
FX_ROW_B = {"utterance": "wiederholung aus", "expected_intent": "LoopAllOffIntent"}


def _ok_response(intent="PlaySongIntent"):
    return {"selectedIntent": {"name": intent, "slots": {}},
            "consideredIntents": [{"name": intent}]}


def _valid_model():
    # ZeroSamplesIntent triggers a validator WARNING (not an error): the happy
    # path must run to completion with warnings present.
    return {"languageModel": {
        "invocationName": "meine sammlung",
        "intents": [
            {"name": "PlaySongIntent", "samples": ["spiele das lied x", "spiele das lied y"]},
            {"name": "AMAZON.StopIntent", "samples": []},
            {"name": "ZeroSamplesIntent"},
        ],
    }}


def _lab_dir(tmp_path):
    LabConfig.save(tmp_path / "lab.json", SKILL_ID, LAB_NAME, LAB_INVOCATION)
    return tmp_path


def _model_file(tmp_path, model):
    p = tmp_path / "model.json"
    p.write_text(json.dumps(model), encoding="utf-8")
    return p


def _fixtures(tmp_path, tests):
    fx = tmp_path / "fixtures"
    fx.mkdir()
    (fx / "de-DE.yaml").write_text(yaml.safe_dump(
        {"locale": "de-DE", "invocation_name": "meine sammlung", "tests": tests}),
        encoding="utf-8")
    return fx


def _workingset(tmp_path, tests, locale="de-DE"):
    p = tmp_path / "ws.yaml"
    p.write_text(yaml.safe_dump({"locale": locale, "tests": tests}), encoding="utf-8")
    return p


def _happy_smapi(monkeypatch, deploy=None, profile=None):
    """Patch the smapi_ops entry points cli calls; returns the ordered call log."""
    log = []
    monkeypatch.setattr(smapi_ops, "get_skill_name", lambda sid: LAB_NAME)

    _BASELINE = {"eTag": "A1", "lastUpdateRequest": {"status": "SUCCEEDED"}}

    def _row(sid, loc):
        log.append(("baseline", sid, loc))
        return dict(_BASELINE)
    monkeypatch.setattr(smapi_ops, "get_build_entry", _row)

    def _deploy(sid, loc, payload):
        log.append(("deploy", sid, loc))
        if deploy is not None:
            return deploy(sid, loc, payload)
        return True
    monkeypatch.setattr(smapi_ops, "deploy_model", _deploy)

    def _wait(sid, loc, timeout_s=600, baseline=None):
        log.append(("wait", sid, loc, baseline))
        return True
    monkeypatch.setattr(smapi_ops, "wait_for_build", _wait)

    def _profile(sid, loc, utterance):
        log.append(("profile", utterance))
        if profile is not None:
            return profile(sid, loc, utterance)
        return _ok_response()
    monkeypatch.setattr(smapi_ops, "profile_row", _profile)
    return log


def _cmd(tmp_path, fixtures, model_path, *extra, label="t"):
    args = cli.build_parser().parse_args(
        ["run", "--locale", "de-DE", "--model", str(model_path), "--label", label, *extra])
    return cli.cmd_run(args, lab_dir=tmp_path, fixtures_dir=fixtures)


def _single_run(tmp_path):
    files = list((tmp_path / "runs").glob("*.json"))
    assert len(files) == 1, f"expected exactly one run file, got {files}"
    return load_run(files[0])


# --- happy path ---

def test_run_happy_path_end_to_end(tmp_path, monkeypatch, capsys):
    monkeypatch.setenv("SMAPI_DELAY", "0")
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A, FX_ROW_B])
    ws = _workingset(tmp_path, [
        {"utterance": "spiele die streithörnchen", "expected_intent": "PlaySongIntent"},
        {"utterance": "spiele musik von die streithörnchen"},  # observation row
    ])
    model_path = _model_file(tmp_path, _valid_model())
    seen = {}

    def deploy(sid, loc, payload):
        seen["payload"] = payload
        # the run file must already exist, in the running state, when deploy fires
        seen["early"] = _single_run(tmp_path)
        return True
    log = _happy_smapi(monkeypatch, deploy=deploy)

    rc = _cmd(lab, fx, model_path, "--workingset", str(ws), label="jf933 lab")
    assert rc == 0
    assert seen["payload"]["interactionModel"]["languageModel"]["invocationName"] == LAB_INVOCATION
    assert seen["early"].status == "running"
    kinds = [e[0] for e in log]
    assert kinds[0] == "baseline" and kinds[1] == "deploy" and kinds[2] == "wait"
    assert kinds.count("baseline") == 3  # witness + post-wait eTag + post-battery bracket
    assert kinds.count("profile") == 4

    run = _single_run(tmp_path)
    assert run.status == "ok" and run.error is None
    assert run.id.endswith("-jf933-lab")
    assert run.locale == "de-DE" and run.label == "jf933 lab"
    assert run.model_source == str(model_path)
    assert run.skill_id == SKILL_ID
    assert len(run.model_sha256) == 64
    assert run.intent_roster == [
        {"intent": "PlaySongIntent", "sample": "spiele das lied x"},
        {"intent": "AMAZON.StopIntent", "sample": None},
        {"intent": "ZeroSamplesIntent", "sample": None},
    ]
    assert [(r.utterance, r.source) for r in run.results] == [
        ("spiele das lied x", "fixture"),
        ("wiederholung aus", "fixture"),
        ("spiele die streithörnchen", "workingset"),
        ("spiele musik von die streithörnchen", "workingset"),
    ]
    assert [r.expected_intent for r in run.results] == [
        "PlaySongIntent", "LoopAllOffIntent", "PlaySongIntent", None]
    assert all(r.status.value == "ok" for r in run.results)
    assert all(r.selected_intent == "PlaySongIntent" for r in run.results)
    assert run.results[0].raw.get("selectedIntent", {}).get("name") == "PlaySongIntent"
    assert run.expectation_failure_count == 1  # FX_ROW_B expects LoopAllOffIntent
    captured = capsys.readouterr()
    assert "expectation failures: 1" in captured.out
    assert "warning" in captured.err.lower()  # ZeroSamplesIntent warned, did not block


def test_run_template_source(tmp_path, monkeypatch):
    monkeypatch.setenv("SMAPI_DELAY", "0")
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A])
    _happy_smapi(monkeypatch)
    built = []

    def fake_build(locale):
        built.append(locale)
        return _valid_model()
    monkeypatch.setattr(models, "build_from_template", fake_build)

    args = cli.build_parser().parse_args(
        ["run", "--locale", "de-DE", "--template", "--label", "tpl"])
    rc = cli.cmd_run(args, lab_dir=lab, fixtures_dir=fx)
    assert rc == 0 and built == ["de-DE"]
    assert _single_run(tmp_path).model_source == "template:de-DE"


def test_run_filter_k_narrows_battery(tmp_path, monkeypatch):
    monkeypatch.setenv("SMAPI_DELAY", "0")
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A, FX_ROW_B])
    _happy_smapi(monkeypatch)
    model_path = _model_file(tmp_path, _valid_model())
    rc = _cmd(lab, fx, model_path, "-k", "wiederholung", label="k")
    assert rc == 0
    run = _single_run(tmp_path)
    assert [r.utterance for r in run.results] == ["wiederholung aus"]


# --- preflight refusals (no deploy, no run file) ---

def test_run_refuses_structural_model_error_before_deploy(tmp_path, monkeypatch, capsys):
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A])
    log = _happy_smapi(monkeypatch)
    broken = {"languageModel": {"invocationName": "meine sammlung", "intents": []}}
    rc = _cmd(lab, fx, _model_file(tmp_path, broken), label="bad")
    assert rc == 1
    assert log == []
    assert not (tmp_path / "runs").exists()
    assert "intents" in capsys.readouterr().err


def test_run_refuses_nonexistent_model_path(tmp_path, monkeypatch, capsys):
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A])
    log = _happy_smapi(monkeypatch)
    rc = _cmd(lab, fx, tmp_path / "nope.json", label="bad")
    assert rc == 1 and log == []
    assert "no such file" in capsys.readouterr().err


def test_run_refuses_nonexistent_workingset_path(tmp_path, monkeypatch, capsys):
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A])
    log = _happy_smapi(monkeypatch)
    model_path = _model_file(tmp_path, _valid_model())
    rc = _cmd(lab, fx, model_path, "--workingset", str(tmp_path / "nope.yaml"), label="bad")
    assert rc == 1 and log == []
    err = capsys.readouterr().err
    assert "workingset" in err and "no such file" in err


def test_run_accepts_nameless_external_model(tmp_path, monkeypatch):
    """A model file without invocationName must pass run preflight: the lab
    always substitutes its own name, so preflight validates the
    POST-substitution payload (a missing SOURCE name is not a structural
    error); the deployed payload carries the lab invocation name. A real
    structural error still refuses (covered by the structural-error test
    below)."""
    monkeypatch.setenv("SMAPI_DELAY", "0")
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A])
    seen = {}

    def deploy(sid, loc, payload):
        seen["payload"] = payload
        return True
    _happy_smapi(monkeypatch, deploy=deploy)
    nameless = {"languageModel": {"intents": [
        {"name": "PlaySongIntent", "samples": ["spiele das lied x", "spiele das lied y"]},
        {"name": "AMAZON.StopIntent", "samples": []},
    ]}}
    rc = _cmd(lab, fx, _model_file(tmp_path, nameless), label="nameless")
    assert rc == 0
    lm = seen["payload"]["interactionModel"]["languageModel"]
    assert lm["invocationName"] == LAB_INVOCATION
    assert _single_run(tmp_path).status == "ok"


def test_run_refuses_workingset_locale_mismatch(tmp_path, monkeypatch, capsys):
    """CLI wiring of the battery-level refusal (the parse and the message live
    in battery.load_battery; see test_battery.py): a locale-mismatched
    workingset still refuses end to end, before any deploy or run file."""
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A])
    ws = _workingset(tmp_path, [{"utterance": "riproduci"}], locale="it-IT")
    log = _happy_smapi(monkeypatch)
    model_path = _model_file(tmp_path, _valid_model())
    rc = _cmd(lab, fx, model_path, "--workingset", str(ws), label="bad")
    assert rc == 1 and log == []
    assert not (tmp_path / "runs").exists()
    err = capsys.readouterr().err
    assert "it-IT" in err and "de-DE" in err


def test_run_refuses_null_utterance_row(tmp_path, monkeypatch, capsys):
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A])
    ws = _workingset(tmp_path, [
        {"utterance": "spiele die streithörnchen", "expected_intent": "PlaySongIntent"},
        {"utterance": None, "expected_intent": "PlaySongIntent"},
    ])
    log = _happy_smapi(monkeypatch)
    model_path = _model_file(tmp_path, _valid_model())
    rc = _cmd(lab, fx, model_path, "--workingset", str(ws), label="bad")
    assert rc == 1 and log == []
    assert not (tmp_path / "runs").exists()
    err = capsys.readouterr().err
    assert "utterance" in err


def test_run_refuses_live_name_mismatch(tmp_path, monkeypatch, capsys):
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A])
    log = _happy_smapi(monkeypatch)
    monkeypatch.setattr(smapi_ops, "get_skill_name", lambda sid: "some other skill")
    rc = _cmd(lab, fx, _model_file(tmp_path, _valid_model()), label="bad")
    assert rc == 1 and log == []
    assert not (tmp_path / "runs").exists()
    assert "pinned" in capsys.readouterr().err


def test_run_refuses_when_live_name_read_fails(tmp_path, monkeypatch, capsys):
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A])
    log = _happy_smapi(monkeypatch)

    def unreadable(sid):
        raise SmapiError("ask smapi get-skill-manifest exited 1: no auth")
    monkeypatch.setattr(smapi_ops, "get_skill_name", unreadable)
    rc = _cmd(lab, fx, _model_file(tmp_path, _valid_model()), label="bad")
    assert rc == 1 and log == []
    assert not (tmp_path / "runs").exists()
    err = capsys.readouterr().err
    assert "live name" in err and "no auth" in err


def test_run_refuses_fixture_row_with_typo_key(tmp_path, monkeypatch, capsys):
    """JF-935 CLI wiring: a typo'd row key in the FIXTURE file used to escape
    _preflight's except (RunRefused, LabConfigError) as a KeyError traceback;
    the battery-level refusal now converts into a clean run refusal naming
    the fixture file and the row index, before any deploy or run file."""
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [
        {"utterance": "spiele das lied x", "expected_intent": "PlaySongIntent"},
        {"utterrence": "spiele das lied y", "expected_intent": "LoopAllOffIntent"},
    ])
    log = _happy_smapi(monkeypatch)
    model_path = _model_file(tmp_path, _valid_model())
    rc = _cmd(lab, fx, model_path, label="bad")
    assert rc == 1 and log == []
    assert not (tmp_path / "runs").exists()
    err = capsys.readouterr().err
    assert "refusing to run" in err and "row #2" in err and "de-DE.yaml" in err


def test_run_refuses_non_mapping_lab_json(tmp_path, monkeypatch, capsys):
    """JF-935: a JSON-valid non-dict lab.json used to raise AttributeError in
    LabConfig.load; run refuses cleanly instead (init-skill never loads
    lab.json, it only refuses an existing one, so run is the crash surface)."""
    (tmp_path / "lab.json").write_text(json.dumps(["not", "a", "dict"]))
    fx = _fixtures(tmp_path, [FX_ROW_A])
    log = _happy_smapi(monkeypatch)
    model_path = _model_file(tmp_path, _valid_model())
    rc = _cmd(tmp_path, fx, model_path, label="bad")
    assert rc == 1 and log == []
    assert not (tmp_path / "runs").exists()
    err = capsys.readouterr().err
    assert "refusing to run" in err and "instead of a mapping" in err


def test_run_refuses_non_dict_interaction_model_value(tmp_path, monkeypatch, capsys):
    """JF-935: {"interactionModel": "oops"} used to crash prepare_payload with
    TypeError after the key-presence unwrap; the models-level ValueError now
    surfaces as a clean run refusal, before any deploy or run file."""
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A])
    log = _happy_smapi(monkeypatch)
    rc = _cmd(lab, fx, _model_file(tmp_path, {"interactionModel": "oops"}), label="bad")
    assert rc == 1 and log == []
    assert not (tmp_path / "runs").exists()
    err = capsys.readouterr().err
    assert "refusing to run" in err and "interactionModel is str" in err


# --- live-phase outcomes (run file rewritten) ---

def test_deploy_exception_saves_failed_run(tmp_path, monkeypatch, capsys):
    monkeypatch.setenv("SMAPI_DELAY", "0")
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A, FX_ROW_B])
    def boom(sid, loc, payload):
        raise SmapiError("ask smapi set-interaction-model (de-DE) exited 1: boom")
    log = _happy_smapi(monkeypatch, deploy=boom)
    rc = _cmd(lab, fx, _model_file(tmp_path, _valid_model()), label="fail")
    assert rc == 1
    run = _single_run(tmp_path)
    assert run.status == "failed" and "boom" in run.error
    assert run.results == []
    assert [e[0] for e in log] == ["baseline", "deploy"]  # wait/battery never ran
    assert "failed" in capsys.readouterr().err


def test_keyboard_interrupt_mid_battery_saves_partial_run(tmp_path, monkeypatch, capsys):
    monkeypatch.setenv("SMAPI_DELAY", "0")
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A, FX_ROW_B])

    def profile(sid, loc, utterance):
        if utterance == FX_ROW_B["utterance"]:
            raise KeyboardInterrupt
        return _ok_response()
    _happy_smapi(monkeypatch, profile=profile)
    rc = _cmd(lab, fx, _model_file(tmp_path, _valid_model()), label="ctrlc")
    assert rc == 130
    run = _single_run(tmp_path)
    assert run.status == "interrupted"
    assert [r.utterance for r in run.results] == [FX_ROW_A["utterance"]]
    assert run.results[0].selected_intent == "PlaySongIntent"
    captured = capsys.readouterr()
    assert "interrupted" in captured.err


# --- entry point ---

def test_main_missing_lab_json_refuses(tmp_path, monkeypatch, capsys):
    monkeypatch.setattr(cli, "LAB_DIR_DEFAULT", tmp_path)
    rc = cli.main(["run", "--locale", "de-DE", "--template", "--label", "x"])
    assert rc == 1
    assert "lab.json" in capsys.readouterr().err


def test_cli_module_entrypoint(tmp_path):
    # --lab-dir redirects the lab dir: init-skill can now create a real
    # lab.json in scripts/nlu-lab, so the refusal must fire against a tmp dir,
    # never against whatever state the production default happens to hold.
    r = subprocess.run([sys.executable, str(CLI_PATH), "run",
                        "--lab-dir", str(tmp_path),
                        "--locale", "de-DE", "--template", "--label", "x"],
                       capture_output=True, text=True)
    assert r.returncode == 1
    assert "lab.json" in r.stderr


def test_run_baseline_read_failure_warns_and_degrades(tmp_path, monkeypatch, capsys):
    """Read failure on the pre-deploy status: loud DEGRADED warning, run proceeds."""
    monkeypatch.setenv("SMAPI_DELAY", "0")
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A, FX_ROW_B])
    _happy_smapi(monkeypatch)

    def _boom(sid, loc):
        raise smapi_ops.SmapiError("ask CLI failed (exit 1)")
    monkeypatch.setattr(smapi_ops, "get_build_entry", _boom)
    rc = _cmd(lab, fx, _model_file(tmp_path, _valid_model()))
    assert rc == 0
    err = capsys.readouterr().err
    assert "DEGRADED" in err and "unreadable" in err


def test_run_baseline_absent_entry_is_silent(tmp_path, monkeypatch, capsys):
    """Fresh skill, no entry yet: baseline None with NO warning (nothing can be stale)."""
    monkeypatch.setenv("SMAPI_DELAY", "0")
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A, FX_ROW_B])
    _happy_smapi(monkeypatch)
    monkeypatch.setattr(smapi_ops, "get_build_entry", lambda sid, loc: None)
    rc = _cmd(lab, fx, _model_file(tmp_path, _valid_model()))
    assert rc == 0
    assert "DEGRADED" not in capsys.readouterr().err


def test_run_threads_pre_deploy_baseline_into_wait(tmp_path, monkeypatch):
    """JF-934: the wait must receive the pre-deploy per-locale entry so a
    stale SUCCEEDED from the previous build can never satisfy it."""
    monkeypatch.setenv("SMAPI_DELAY", "0")
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A, FX_ROW_B])
    model_path = _model_file(tmp_path, _valid_model())
    log = _happy_smapi(monkeypatch)
    rc = _cmd(lab, fx, model_path)
    assert rc == 0
    waits = [e for e in log if e[0] == "wait"]
    assert len(waits) == 1
    assert waits[0][3] == {"eTag": "A1", "lastUpdateRequest": {"status": "SUCCEEDED"}}
    baselines = [e for e in log if e[0] == "baseline"]
    assert len(baselines) == 3 and all(b[1] == "amzn1.ask.skill.lab" for b in baselines)


def test_run_skip_deploy_never_deploys_nor_waits(tmp_path, monkeypatch, capsys):
    """--skip-deploy is the A/A instrument: probe the already-deployed model;
    deploy and build-wait must be skipped (the one build-entry read at start
    is the identity capture, not a baseline witness)."""
    monkeypatch.setenv("SMAPI_DELAY", "0")
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A, FX_ROW_B])
    log = _happy_smapi(monkeypatch)
    args = cli.build_parser().parse_args(
        ["run", "--locale", "de-DE", "--model", str(_model_file(tmp_path, _valid_model())),
         "--label", "aa2", "--skip-deploy"])
    rc = cli.cmd_run(args, lab_dir=tmp_path, fixtures_dir=fx)
    assert rc == 0
    kinds = [e[0] for e in log]
    assert "deploy" not in kinds and "wait" not in kinds
    assert kinds.count("baseline") == 2  # identity capture + post-battery bracket
    assert kinds.count("profile") == 2
    assert "skip-deploy" in capsys.readouterr().out


def test_run_marks_tainted_when_etag_changes_mid_battery(tmp_path, monkeypatch, capsys):
    """JF-941: a concurrent redeploy during the battery mixes two models in one
    run; the post-battery eTag bracket detects and marks it tainted."""
    monkeypatch.setenv("SMAPI_DELAY", "0")
    lab = _lab_dir(tmp_path)
    fx = _fixtures(tmp_path, [FX_ROW_A, FX_ROW_B])
    log = _happy_smapi(monkeypatch)
    # The deploy-path fake returns a fixed entry; the post-battery check will
    # see a DIFFERENT eTag (the fake get_build_entry alternates on call count)
    calls = []
    etags = ["E-1", "E-1", "E-2"]  # pre-deploy capture, post-wait capture, post-battery
    def _entry(sid, loc):
        i = min(len(calls), len(etags) - 1)
        etag = etags[i]
        calls.append(1)
        return {"eTag": etag, "lastUpdateRequest": {"status": "SUCCEEDED"}}
    monkeypatch.setattr(smapi_ops, "get_build_entry", _entry)
    rc = _cmd(lab, fx, _model_file(tmp_path, _valid_model()))
    assert rc == 1  # tainted exits 1
    err = capsys.readouterr().err
    assert "TAINTED" in err and "E-1" in err and "E-2" in err
    run = _single_run(tmp_path)
    assert run.status == "tainted"
    assert "eTag E-1" in run.error


def test_run_live_probes_real_skill_no_lab_guard(tmp_path, monkeypatch, capsys):
    """JF-944: --live profiles the real skill, skips the lab.json guard,
    and writes a normal run file with the live skill's ID."""
    monkeypatch.setenv("SMAPI_DELAY", "0")
    lab = _lab_dir(tmp_path)  # no lab.json created on purpose
    fx = _fixtures(tmp_path, [FX_ROW_A, FX_ROW_B])
    model_path = _model_file(tmp_path, _valid_model())

    # Mock the live skill discovery
    monkeypatch.setattr(cli, "_discover_live_skill_id",
                        lambda: "amzn1.ask.skill.LIVE-001")
    log = _happy_smapi(monkeypatch)

    args = cli.build_parser().parse_args(
        ["run", "--locale", "de-DE", "--model", str(model_path),
         "--label", "live-check", "--live"])
    rc = cli.cmd_run(args, lab_dir=lab, fixtures_dir=fx)
    assert rc == 0
    out = capsys.readouterr().out
    assert "live:" in out and "LIVE-001" in out
    kinds = [e[0] for e in log]
    assert "deploy" not in kinds and "wait" not in kinds and "baseline" not in kinds
    assert kinds.count("profile") >= 2  # at least one per battery row
    run = _single_run(lab)
    assert run.skill_id == "amzn1.ask.skill.LIVE-001"
    assert run.model_source.startswith("live:")
    assert run.status == "ok"
    assert len(run.results) == 2  # the two fixture rows
