"""JF-912: the fixture invocation_name header is an optional override.

The composer's invocation name now derives from the committed model JSON by
default (model_<locale>.json invocationName, read through nlu_static's
ONE loader), and a fixture header only overrides when present. The tests
here pin the three load-bearing pieces:

- the conftest model-name reader (_model_invocation_name): model read,
  per-session cache on the production path, explicit-dir bypass, and the
  loud failure when no usable model name exists;
- the override precedence through load_locale_fixtures on tmp fixture and
  model trees (present header wins; absent or empty falls back to the
  model; no model fails at load, never silently composes);
- the parity lint's narrowed contract: an absent or empty header is the
  supported default and never warns, a present-but-stale override still
  warns.

The integration conftest module is loaded fresh per test via importlib
(the JF-954.7 pattern), which also gives every test an empty name cache.
No test reaches the network, SMAPI or the ask CLI; the committed-model
tests are read-only real-data touches.
"""

import importlib.util
import json
import sys
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO / "scripts"
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))
TESTS_INTEGRATION_DIR = REPO / "tests" / "integration"
if str(TESTS_INTEGRATION_DIR) not in sys.path:
    sys.path.insert(0, str(TESTS_INTEGRATION_DIR))

from nlu_static.effective_model import (  # noqa: E402
    DEFAULT_MODELS_DIR,
    load_locale_model,
)
from smapi_client import SmapiClient  # noqa: E402
from validate_interaction_models import lint_fixture_invocation_names  # noqa: E402

LOCALE = "xx-XX"


# ---------------------------------------------------------------- helpers


def _load_integration_conftest():
    """The integration conftest module, loaded fresh (empty name cache)."""
    path = REPO / "tests" / "integration" / "conftest.py"
    spec = importlib.util.spec_from_file_location("jf912_integration_conftest", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def _write_model(model_dir: Path, locale: str, model: dict) -> None:
    """A synthetic committed model_<locale>.json (the bare languageModel shape)."""
    model_dir.mkdir(parents=True, exist_ok=True)
    (model_dir / f"model_{locale}.json").write_text(json.dumps(model), encoding="utf-8")


def _write_fixture(
    fixtures_dir: Path,
    name: str,
    locale: str,
    invocation: str | None,
) -> None:
    """One fixture file; ``invocation=None`` omits the header entirely."""
    fixtures_dir.mkdir(parents=True, exist_ok=True)
    lines = [f"locale: {locale}"]
    if invocation is not None:
        lines.append(f'invocation_name: "{invocation}"')
    lines += [
        "tests:",
        '  - utterance: "una riga"',
        "    expected_intent: FooIntent",
    ]
    (fixtures_dir / name).write_text("\n".join(lines) + "\n", encoding="utf-8")


# ------------------------------------------------- the model-name reader


def test_model_invocation_name_reads_the_committed_model():
    conftest = _load_integration_conftest()
    # The pin is against the model itself, not a hardcoded string: whatever
    # the committed it-IT model says, the loader returns exactly that.
    expected = load_locale_model("it-IT")["invocationName"]
    assert expected
    assert conftest._model_invocation_name("it-IT") == expected


def test_every_committed_locale_resolves_a_name():
    conftest = _load_integration_conftest()
    model_paths = sorted(DEFAULT_MODELS_DIR.glob("model_*.json"))
    assert len(model_paths) == 17
    for path in model_paths:
        locale = path.stem.removeprefix("model_")
        name = conftest._model_invocation_name(locale)
        assert isinstance(name, str) and name.strip(), f"{locale}: empty name"


def test_model_invocation_name_caches_once_per_locale(monkeypatch):
    conftest = _load_integration_conftest()
    calls: list[tuple[str, object]] = []

    def counting_loader(locale, models_dir=None):
        calls.append((locale, models_dir))
        return {"invocationName": "stub name"}

    monkeypatch.setattr(conftest, "_load_locale_model", counting_loader)

    assert conftest._model_invocation_name(LOCALE) == "stub name"
    assert conftest._model_invocation_name(LOCALE) == "stub name"
    assert calls == [(LOCALE, None)], "the production path must read once"

    # An explicit models_dir is the test path: it bypasses the cache in
    # both directions (always re-reads, never pollutes).
    assert conftest._model_invocation_name(LOCALE, models_dir="/tmp/xx") == "stub name"
    assert calls[-1] == (LOCALE, "/tmp/xx")
    assert len(calls) == 2


def test_missing_model_fails_loudly(tmp_path):
    conftest = _load_integration_conftest()
    with pytest.raises(RuntimeError, match="xx-XX"):
        conftest._model_invocation_name(LOCALE, models_dir=tmp_path)


def test_model_without_a_name_fails_loudly(tmp_path):
    conftest = _load_integration_conftest()
    _write_model(tmp_path, LOCALE, {"intents": [], "types": []})
    with pytest.raises(RuntimeError, match="no usable invocationName"):
        conftest._model_invocation_name(LOCALE, models_dir=tmp_path)


def test_model_with_an_empty_name_fails_loudly(tmp_path):
    conftest = _load_integration_conftest()
    _write_model(tmp_path, LOCALE, {"invocationName": "  ", "intents": [], "types": []})
    with pytest.raises(RuntimeError, match="no usable invocationName"):
        conftest._model_invocation_name(LOCALE, models_dir=tmp_path)


# ------------------------------------------------- the override precedence


def test_present_header_wins_over_the_model(tmp_path):
    conftest = _load_integration_conftest()
    model_dir = tmp_path / "models"
    fixtures_dir = tmp_path / "fixtures"
    _write_model(model_dir, LOCALE, {"invocationName": "model name", "intents": [], "types": []})
    _write_fixture(fixtures_dir, f"{LOCALE}.yaml", LOCALE, "header name")

    cases = conftest.load_locale_fixtures(fixtures_dir, models_dir=model_dir)
    assert [c["invocation_name"] for c in cases] == ["header name"]


def test_absent_header_falls_back_to_the_model(tmp_path):
    conftest = _load_integration_conftest()
    model_dir = tmp_path / "models"
    fixtures_dir = tmp_path / "fixtures"
    _write_model(model_dir, LOCALE, {"invocationName": "model name", "intents": [], "types": []})
    _write_fixture(fixtures_dir, f"{LOCALE}.yaml", LOCALE, None)

    cases = conftest.load_locale_fixtures(fixtures_dir, models_dir=model_dir)
    assert [c["invocation_name"] for c in cases] == ["model name"]


def test_empty_header_falls_back_to_the_model(tmp_path):
    conftest = _load_integration_conftest()
    model_dir = tmp_path / "models"
    fixtures_dir = tmp_path / "fixtures"
    _write_model(model_dir, LOCALE, {"invocationName": "model name", "intents": [], "types": []})
    _write_fixture(fixtures_dir, f"{LOCALE}.yaml", LOCALE, "")

    cases = conftest.load_locale_fixtures(fixtures_dir, models_dir=model_dir)
    assert [c["invocation_name"] for c in cases] == ["model name"]


def test_headerless_fixture_without_a_model_fails_loudly(tmp_path):
    conftest = _load_integration_conftest()
    model_dir = tmp_path / "models"
    fixtures_dir = tmp_path / "fixtures"
    _write_fixture(fixtures_dir, f"{LOCALE}.yaml", LOCALE, None)

    with pytest.raises(RuntimeError, match="xx-XX"):
        conftest.load_locale_fixtures(fixtures_dir, models_dir=model_dir)


def test_missing_fixture_dir_stays_an_empty_noop(tmp_path):
    conftest = _load_integration_conftest()
    # No fixtures, no model dir: the early return must not touch the
    # model reader (pre-JF-912 behavior for an absent tree).
    assert conftest.load_locale_fixtures(tmp_path / "nope", models_dir=tmp_path) == []


# ------------------------------------ the composer end to end (tmp tree)

# The e2e one-shot composer shape under test, mirroring
# tests/integration/test_e2e.py's e2e_smapi_client fixture: the case dict
# from load_locale_fixtures feeds SmapiClient's invocation_name, and
# simulate() prefixes the locale's invocation pattern with it. The stub
# replaces only the ask-CLI subprocess; composition, rate-limit delay and
# the response path stay the real code.


def _composed_one_shot(tmp_path, monkeypatch, invocation, utterance="una riga"):
    """load_locale_fixtures + SmapiClient.simulate on a tmp tree -> utterance sent."""
    conftest = _load_integration_conftest()
    model_dir = tmp_path / "models"
    fixtures_dir = tmp_path / "fixtures"
    _write_model(model_dir, "it-IT", {"invocationName": "model nome", "intents": [], "types": []})
    _write_fixture(fixtures_dir, "e2e_it-IT.yaml", "it-IT", invocation)

    cases = conftest.load_locale_fixtures(
        fixtures_dir,
        prefix="e2e_",
        exclude_prefixes=("e2e_reliability_", "e2e_smoke_"),
        models_dir=model_dir,
    )
    assert len(cases) == 1
    case = cases[0]
    client = SmapiClient(
        skill_id="skill", locale=case["locale"], delay=0.0,
        invocation_name=case.get("invocation_name", ""),
    )
    sent: list[str] = []

    def capture(self, full_utterance, stage):
        sent.append(full_utterance)
        return {"id": "sim", "status": "SUCCESSFUL"}

    monkeypatch.setattr(SmapiClient, "_initiate_simulation", capture)
    client.simulate(utterance)
    assert len(sent) == 1
    return sent[0]


def test_headerless_fixture_composes_with_the_model_name(tmp_path, monkeypatch):
    composed = _composed_one_shot(tmp_path, monkeypatch, invocation=None)
    assert composed == "chiedi a model nome di una riga"


def test_header_override_composes_with_the_header_name(tmp_path, monkeypatch):
    composed = _composed_one_shot(tmp_path, monkeypatch, invocation="header nome")
    assert composed == "chiedi a header nome di una riga"


# ------------------------------------------------------- the parity lint


def _lint(tmp_path, invocation, name="xx-XX.yaml"):
    _write_fixture(tmp_path, name, LOCALE, invocation)
    return lint_fixture_invocation_names(
        {LOCALE: {"invocationName": "model name"}}, fixtures_dir=tmp_path
    )


def test_lint_stale_override_still_warns(tmp_path):
    warnings = _lint(tmp_path, "header name")
    assert warnings and "stale override" in warnings[0]


def test_lint_absent_header_is_the_supported_default(tmp_path):
    assert _lint(tmp_path, None) == []


def test_lint_empty_header_is_the_supported_default(tmp_path):
    assert _lint(tmp_path, "") == []


def test_lint_matching_header_is_clean(tmp_path):
    assert _lint(tmp_path, "model name") == []


def test_lint_headerless_e2e_family_is_clean(tmp_path):
    assert _lint(tmp_path, None, name=f"e2e_smoke_{LOCALE}.yaml") == []
