"""Import-safety spike: every repo module the lab reuses must import without side effects."""
import importlib
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]

def _sys_path():
    for p in (REPO / "scripts", REPO / "tests" / "integration", REPO / "scripts" / "nlu-lab"):
        s = str(p)
        if s not in sys.path:
            sys.path.insert(0, s)

def test_imports_have_no_side_effects(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)  # importing must not depend on cwd, and must not write files
    _sys_path()
    before = set(p.name for p in Path.cwd().iterdir())
    for name in ("nlu_wrapper_battery", "smapi_client", "validate_interaction_models", "generate_interaction_model"):
        importlib.import_module(name)
    after = set(p.name for p in Path.cwd().iterdir())
    assert before == after

def test_signatures_the_spec_pins():
    _sys_path()
    import validate_interaction_models as v
    import generate_interaction_model as g
    import nlu_wrapper_battery as w
    assert callable(v.validate_single_model)
    assert callable(g.build_model) and callable(g.serialize_model)
    assert callable(w.profile_nlu) and callable(w._run_ask)
