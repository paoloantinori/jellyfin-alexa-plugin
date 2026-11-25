import json
import pytest
from lab_config import LabConfig, LabConfigError

def _write(tmp_path, data):
    p = tmp_path / "lab.json"
    p.write_text(json.dumps(data))
    return p

def test_missing_file_refuses(tmp_path):
    with pytest.raises(LabConfigError, match="refuse"):
        LabConfig.load(tmp_path / "lab.json")

def test_unparseable_refuses(tmp_path):
    p = tmp_path / "lab.json"; p.write_text("{not json")
    with pytest.raises(LabConfigError, match="refuse"):
        LabConfig.load(p)

def test_undecodable_lab_json_refuses(tmp_path):
    """JF-956: a binary/undecodable lab.json raised UnicodeDecodeError from
    read_text, escaping the JSONDecodeError-only except as a raw traceback;
    the refusal keeps the same 'refuses rather than re-init' text shape."""
    p = tmp_path / "lab.json"; p.write_bytes(b"\xff\x00binary not utf-8")
    with pytest.raises(LabConfigError, match="refuses rather than re-init"):
        LabConfig.load(p)

def test_non_mapping_refuses(tmp_path):
    """JF-935: a JSON-valid non-dict lab.json (e.g. the list
    ["not","a","dict"]) used to raise AttributeError on data.get; the module
    docstring promises 'missing/unparseable refuses, never re-inits', so the
    shape check refuses with LabConfigError instead."""
    with pytest.raises(LabConfigError, match=r"holds a JSON list instead of a mapping"):
        LabConfig.load(_write(tmp_path, ["not", "a", "dict"]))

def test_loads_and_pins(tmp_path):
    p = _write(tmp_path, {"skill_id": "amzn1.ask.skill.1", "skill_name": "jellyfin nlu lab", "invocation_name": "jellyfin nlu laboratorio"})
    cfg = LabConfig.load(p)
    assert cfg.skill_id == "amzn1.ask.skill.1" and cfg.skill_name == "jellyfin nlu lab"
    assert cfg.invocation_name == "jellyfin nlu laboratorio"

def test_missing_invocation_name_refuses(tmp_path):
    with pytest.raises(LabConfigError, match="invocation"):
        LabConfig.load(_write(tmp_path, {"skill_id": "s", "skill_name": "n"}))

def test_guard_rejects_foreign_skill(tmp_path):
    cfg = LabConfig.load(_write(tmp_path, {"skill_id": "amzn1.ask.skill.1", "skill_name": "n", "invocation_name": "jellyfin nlu laboratorio"}))
    with pytest.raises(LabConfigError, match="lab skill"):
        cfg.guard_deploy_target("amzn1.ask.skill.OTHER")

def test_guard_rejects_live_name_mismatch(tmp_path):
    cfg = LabConfig.load(_write(tmp_path, {"skill_id": "amzn1.ask.skill.1", "skill_name": "lab name", "invocation_name": "jellyfin nlu laboratorio"}))
    with pytest.raises(LabConfigError, match="pinned"):
        cfg.guard_deploy_target("amzn1.ask.skill.1", live_skill_name="some other skill")
