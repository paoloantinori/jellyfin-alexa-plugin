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
