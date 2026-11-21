"""cmd_init orchestration: the ask runner is monkeypatched at smapi_ops._run_ask
(no network); prepare_manifest runs against the REAL committed manifest template
(a repo file read, never a network read). The create path pins the exact
create-skill-for-vendor argv (no --vendor-id: the installed CLI has no such
flag and resolves the vendor from its own profile state); --pin-id asserts the
live-name verification; the overwrite refusal asserts no ask call ever fires.
"""
import json
import subprocess
from pathlib import Path

import pytest

import _paths
_paths.bootstrap()

import cli
import smapi_ops
from lab_config import LabConfig
from validate_versions import get_props_version

SKILL_ID = "amzn1.ask.skill.created-by-init"
PINNED_ID = "amzn1.ask.skill.console-made"
SKILL_NAME = "jellyfin nlu lab"
INVOCATION = "jellyfin nlu laboratorio"
ENDPOINT = "https://lab.example.com/alexaskill"
TEMPLATE = cli.MANIFEST_TEMPLATE_DEFAULT


def _completed(argv, rc=0, stdout="", stderr=""):
    return subprocess.CompletedProcess(argv, rc, stdout, stderr)


def _manifest_stdout(name=SKILL_NAME):
    return json.dumps({"manifest": {"publishingInformation":
                                    {"locales": {"en-US": {"name": name}}}}})


def _create_runner(payloads, skill_id=SKILL_ID, available=True):
    """Fake _run_ask for the create path: answers the local --help availability
    probe, and on the create call reads the file: manifest while the tmp file
    still exists (create_skill unlinks it before returning)."""
    def runner(argv):
        if argv[1:3] != ["smapi", "create-skill-for-vendor"]:
            return _completed(argv, 1, stderr=f"unexpected ask call: {argv}")
        if "--help" in argv:
            if available:
                return _completed(argv, 0)
            return _completed(argv, 1, stderr='Command not recognized. Please run "ask smapi" '
                                              'to check the user instructions.')
        token = argv[argv.index("--manifest") + 1]
        assert token.startswith("file:"), f"expected file: manifest, got {token}"
        payloads.append(json.loads(Path(token[len("file:"):]).read_text(encoding="utf-8")))
        return _completed(argv, 0, stdout=json.dumps({"skillId": skill_id}))
    return runner


def _manifest_runner(name, calls):
    def runner(argv):
        calls.append(list(argv))
        if argv[1:3] == ["smapi", "get-skill-manifest"]:
            return _completed(argv, 0, stdout=_manifest_stdout(name))
        return _completed(argv, 1, stderr=f"unexpected ask call: {argv}")
    return runner


# --- prepare_manifest (the REAL committed template) ---

def test_prepare_manifest_stamps_the_committed_template():
    template_before = TEMPLATE.read_text(encoding="utf-8")
    template = json.loads(template_before)["manifest"]

    out = cli.prepare_manifest(endpoint_uri=ENDPOINT,
                               events_uri=ENDPOINT + "/events",
                               version="9.9.9.9", skill_name=SKILL_NAME)

    manifest = out["manifest"]
    # custom endpoint INSERTED (absent from the committed template)
    assert "endpoint" not in template["apis"]["custom"]
    assert manifest["apis"]["custom"]["endpoint"] == {"uri": ENDPOINT}
    # events endpoint FILLED (the template ships it empty)
    assert template["events"]["endpoint"]["uri"] == ""
    assert manifest["events"]["endpoint"]["uri"] == ENDPOINT + "/events"
    # version stamped into testingInstructions
    assert "9.9.9.9" in manifest["publishingInformation"]["testingInstructions"]
    # every publishing locale name stamped, no locale dropped
    locales = manifest["publishingInformation"]["locales"]
    assert len(locales) == 17
    assert set(locales) == set(template["publishingInformation"]["locales"])
    assert all(entry["name"] == SKILL_NAME for entry in locales.values())
    # the committed template on disk is untouched
    assert TEMPLATE.read_text(encoding="utf-8") == template_before


def test_prepare_manifest_refuses_non_template_shapes(tmp_path):
    with pytest.raises(cli.InitRefused):
        cli.prepare_manifest(ENDPOINT, ENDPOINT, "1.0.0.0", SKILL_NAME,
                             template_path=tmp_path / "missing.json")
    bad = tmp_path / "bad.json"
    bad.write_text(json.dumps({"nope": 1}), encoding="utf-8")
    with pytest.raises(cli.InitRefused):
        cli.prepare_manifest(ENDPOINT, ENDPOINT, "1.0.0.0", SKILL_NAME, template_path=bad)
    nonlocale = tmp_path / "nonlocale.json"
    nonlocale.write_text(json.dumps({"manifest": {
        "apis": {"custom": {}}, "events": {"endpoint": {}},
        "publishingInformation": {"testingInstructions": "",
                                  "locales": {"en-US": "jellyfin"}}}}), encoding="utf-8")
    with pytest.raises(cli.InitRefused):
        cli.prepare_manifest(ENDPOINT, ENDPOINT, "1.0.0.0", SKILL_NAME,
                             template_path=nonlocale)


# --- create path ---

def test_init_creates_skill_and_writes_lab_json(tmp_path, monkeypatch, capsys):
    payloads = []
    calls = []
    inner = _create_runner(payloads)

    def logging_runner(argv):
        calls.append(list(argv))
        return inner(argv)
    monkeypatch.setattr(smapi_ops, "_run_ask", logging_runner)

    args = cli.build_parser().parse_args(
        ["init-skill", "--name", SKILL_NAME, "--invocation", INVOCATION,
         "--endpoint", ENDPOINT])
    rc = cli.cmd_init(args, lab_dir=tmp_path)
    assert rc == 0

    cfg = LabConfig.load(tmp_path / "lab.json")
    assert (cfg.skill_id, cfg.skill_name, cfg.invocation_name) == \
        (SKILL_ID, SKILL_NAME, INVOCATION)

    probes = [c for c in calls if "--help" in c]
    creates = [c for c in calls if "--help" not in c]
    assert len(probes) == 1 and len(creates) == 1
    argv = creates[0]
    assert argv[:3] == ["ask", "smapi", "create-skill-for-vendor"]
    assert "--vendor-id" not in argv  # no such flag on the installed CLI

    manifest = payloads[0]["manifest"]
    assert manifest["apis"]["custom"]["endpoint"]["uri"] == ENDPOINT
    assert manifest["events"]["endpoint"]["uri"] == ENDPOINT
    assert all(e["name"] == SKILL_NAME
               for e in manifest["publishingInformation"]["locales"].values())
    assert get_props_version() in manifest["publishingInformation"]["testingInstructions"]
    assert "created" in capsys.readouterr().out.lower()


def test_init_refuses_overwrite_without_force(tmp_path, monkeypatch, capsys):
    LabConfig.save(tmp_path / "lab.json", "amzn1.ask.skill.old", "old name", "old invocation")
    calls = []

    def runner(argv):
        calls.append(list(argv))
        return _completed(argv)
    monkeypatch.setattr(smapi_ops, "_run_ask", runner)

    args = cli.build_parser().parse_args(["init-skill", "--endpoint", ENDPOINT])
    rc = cli.cmd_init(args, lab_dir=tmp_path)
    assert rc == 1
    assert calls == []  # refused before any ask call
    assert "force" in capsys.readouterr().err
    cfg = LabConfig.load(tmp_path / "lab.json")
    assert cfg.skill_id == "amzn1.ask.skill.old"  # existing lab.json untouched


def test_init_force_overwrites_lab_json(tmp_path, monkeypatch):
    LabConfig.save(tmp_path / "lab.json", "amzn1.ask.skill.old", "old name", "old invocation")
    payloads = []
    monkeypatch.setattr(smapi_ops, "_run_ask", _create_runner(payloads))
    args = cli.build_parser().parse_args(["init-skill", "--endpoint", ENDPOINT, "--force"])
    rc = cli.cmd_init(args, lab_dir=tmp_path)
    assert rc == 0
    cfg = LabConfig.load(tmp_path / "lab.json")
    assert cfg.skill_id == SKILL_ID
    assert cfg.skill_name == cli.DEFAULT_SKILL_NAME


def test_init_prints_console_fallback_when_subcommand_missing(tmp_path, monkeypatch, capsys):
    monkeypatch.setattr(smapi_ops, "_run_ask",
                        _create_runner([], available=False))
    args = cli.build_parser().parse_args(["init-skill", "--endpoint", ENDPOINT])
    rc = cli.cmd_init(args, lab_dir=tmp_path)
    assert rc == 2
    out = capsys.readouterr().out
    assert "--pin-id" in out
    assert cli.DEFAULT_SKILL_NAME in out  # the console skill must carry the pinned name
    assert not (tmp_path / "lab.json").exists()


def test_init_requires_endpoint_on_create_path(tmp_path, monkeypatch, capsys):
    calls = []

    def runner(argv):
        calls.append(list(argv))
        return _completed(argv)
    monkeypatch.setattr(smapi_ops, "_run_ask", runner)
    args = cli.build_parser().parse_args(["init-skill"])
    rc = cli.cmd_init(args, lab_dir=tmp_path)
    assert rc == 1
    assert calls == []  # refused before the availability probe or any create
    assert "--endpoint" in capsys.readouterr().err


# --- --pin-id path (console-created skill) ---

def test_init_pin_id_records_skill_when_live_name_matches(tmp_path, monkeypatch, capsys):
    calls = []
    monkeypatch.setattr(smapi_ops, "_run_ask", _manifest_runner(SKILL_NAME, calls))
    args = cli.build_parser().parse_args(["init-skill", "--pin-id", PINNED_ID])
    rc = cli.cmd_init(args, lab_dir=tmp_path)
    assert rc == 0
    cfg = LabConfig.load(tmp_path / "lab.json")
    assert (cfg.skill_id, cfg.skill_name) == (PINNED_ID, SKILL_NAME)
    assert cfg.invocation_name == cli.DEFAULT_INVOCATION
    argv = calls[0]
    assert argv[1:3] == ["smapi", "get-skill-manifest"]
    assert PINNED_ID in argv and "development" in argv
    assert "pinned" in capsys.readouterr().out.lower()


def test_init_pin_id_refuses_live_name_mismatch(tmp_path, monkeypatch, capsys):
    monkeypatch.setattr(smapi_ops, "_run_ask", _manifest_runner("some other skill", []))
    args = cli.build_parser().parse_args(["init-skill", "--pin-id", PINNED_ID])
    rc = cli.cmd_init(args, lab_dir=tmp_path)
    assert rc == 1
    assert not (tmp_path / "lab.json").exists()
    err = capsys.readouterr().err
    assert "some other skill" in err and cli.DEFAULT_SKILL_NAME in err


def test_init_pin_id_read_failure_refuses(tmp_path, monkeypatch, capsys):
    def broken(argv):
        return _completed(argv, 1, stderr="ask smapi get-skill-manifest exited 1: no auth")
    monkeypatch.setattr(smapi_ops, "_run_ask", broken)
    args = cli.build_parser().parse_args(["init-skill", "--pin-id", PINNED_ID])
    rc = cli.cmd_init(args, lab_dir=tmp_path)
    assert rc == 1
    assert not (tmp_path / "lab.json").exists()
    err = capsys.readouterr().err
    assert "live name" in err and "no auth" in err


def test_init_create_call_timeout_refuses_cleanly(tmp_path, monkeypatch, capsys):
    """The runner's TimeoutExpired (120s) must become the clean refusal, not a
    raw traceback out of cmd_init (the a096928b TimeoutExpired-tolerance class)."""

    def runner(argv):
        if "--help" in argv:
            return _completed(argv)
        raise subprocess.TimeoutExpired(cmd=argv, timeout=120)
    monkeypatch.setattr(smapi_ops, "_run_ask", runner)
    args = cli.build_parser().parse_args(["init-skill", "--endpoint", ENDPOINT])
    rc = cli.cmd_init(args, lab_dir=tmp_path)
    assert rc == 1
    assert not (tmp_path / "lab.json").exists()
    err = capsys.readouterr().err
    assert "refusing to init" in err and "timed out" in err
