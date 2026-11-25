"""JF-912: the fixture invocation_name header is an optional override.

The composer's invocation name now derives from the committed model JSON by
default (model_<locale>.json invocationName, read through nlu_static's
ONE loader), and a fixture header only overrides when present. The tests
here pin the load-bearing pieces:

- the conftest model-name reader (_model_invocation_name): model read,
  per-session cache on the production path, explicit-dir bypass, the
  STRIPPED value, and the loud failure when no usable model name exists;
- the override precedence through load_locale_fixtures on tmp fixture and
  model trees (present header wins; absent or empty falls back to the
  model; no model fails at load, never silently composes; a present
  NON-STRING header refuses loudly, review round 2 finding 1);
- the parity lint's narrowed contract: an absent or empty header is the
  supported default and never warns, a present-but-stale override still
  warns, a non-string header warns (finding 1), and each smoke fixture's
  open_utterance must CONTAIN the model name (finding 2, the embedded
  copy no mechanism derives);
- the wrapper battery containment (finding 3): every simulate row and
  evergreen control contains its model's invocationName; profile rows are
  never checked.

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
from validate_interaction_models import (  # noqa: E402
    FIXTURES_DIR,
    lint_fixture_invocation_names,
    lint_wrapper_battery_invocation,
)

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


def test_padded_model_name_is_returned_and_cached_stripped(tmp_path, monkeypatch):
    # Review round 2, finding 5: the strip is the cached and returned value,
    # not only the usability gate.
    conftest = _load_integration_conftest()
    _write_model(tmp_path, LOCALE, {"invocationName": "  padded name  ", "intents": [], "types": []})
    assert conftest._model_invocation_name(LOCALE, models_dir=tmp_path) == "padded name"

    calls: list[str] = []
    real = conftest._load_locale_model

    def counting(locale, models_dir=None):
        calls.append(locale)
        return real(locale, models_dir)

    monkeypatch.setattr(conftest, "_load_locale_model", counting)
    assert conftest._model_invocation_name(LOCALE, models_dir=tmp_path) == "padded name"
    assert calls, "the explicit-dir test path re-reads (no cache pollution)"


def test_non_string_header_refuses_loudly(tmp_path):
    # Review round 2, finding 1: a present-but-non-string header is a
    # fixture bug, not a missing override; the old lint warned for exactly
    # this shape and the loader now refuses it at collection.
    conftest = _load_integration_conftest()
    fixtures_dir = tmp_path / "fixtures"
    fixtures_dir.mkdir()
    (fixtures_dir / f"{LOCALE}.yaml").write_text(
        f"locale: {LOCALE}\n"
        "invocation_name: 42\n"
        "tests:\n"
        '  - utterance: "una riga"\n'
        "    expected_intent: FooIntent\n",
        encoding="utf-8",
    )
    with pytest.raises(RuntimeError, match=r"non-string invocation_name header \(int\)"):
        conftest.load_locale_fixtures(fixtures_dir, models_dir=tmp_path)


def test_non_string_list_header_refuses_loudly(tmp_path):
    conftest = _load_integration_conftest()
    fixtures_dir = tmp_path / "fixtures"
    fixtures_dir.mkdir()
    (fixtures_dir / f"{LOCALE}.yaml").write_text(
        f"locale: {LOCALE}\n"
        "invocation_name: [a, b]\n"
        "tests:\n"
        '  - utterance: "una riga"\n'
        "    expected_intent: FooIntent\n",
        encoding="utf-8",
    )
    with pytest.raises(RuntimeError, match=r"non-string invocation_name header \(list\)"):
        conftest.load_locale_fixtures(fixtures_dir, models_dir=tmp_path)


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


def test_lint_non_string_header_warns(tmp_path):
    # Review round 2, finding 1, the lint side: the old lint warned for a
    # non-string header, so the narrowed lint must keep seeing it.
    _write_fixture(tmp_path, f"{LOCALE}.yaml", LOCALE, None)
    (tmp_path / f"{LOCALE}.yaml").write_text(
        f"locale: {LOCALE}\n"
        "invocation_name: true\n"
        "tests:\n"
        '  - utterance: "una riga"\n'
        "    expected_intent: FooIntent\n",
        encoding="utf-8",
    )
    warnings = lint_fixture_invocation_names(
        {LOCALE: {"invocationName": "model name"}}, fixtures_dir=tmp_path
    )
    assert len(warnings) == 1
    assert "invocation_name header is a bool, not a string" in warnings[0]


def test_lint_stale_smoke_open_warns(tmp_path):
    # Review round 2, finding 2: the smoke open embeds the invocation name
    # as authored text; a model rename leaves it stale and only this lint
    # sees it.
    _write_fixture(tmp_path, f"e2e_smoke_{LOCALE}.yaml", LOCALE, None)
    (tmp_path / f"e2e_smoke_{LOCALE}.yaml").write_text(
        f"locale: {LOCALE}\n"
        'open_utterance: "open the old name"\n'
        "tests:\n"
        '  - utterance: "una riga"\n'
        "    expected_intent: FooIntent\n",
        encoding="utf-8",
    )
    warnings = lint_fixture_invocation_names(
        {LOCALE: {"invocationName": "model name"}}, fixtures_dir=tmp_path
    )
    assert len(warnings) == 1
    assert "open_utterance does not contain the model invocationName" in warnings[0]


def test_lint_matching_smoke_open_is_clean(tmp_path):
    _write_fixture(tmp_path, f"e2e_smoke_{LOCALE}.yaml", LOCALE, None)
    (tmp_path / f"e2e_smoke_{LOCALE}.yaml").write_text(
        f"locale: {LOCALE}\n"
        'open_utterance: "open model name now"\n'
        "tests:\n"
        '  - utterance: "una riga"\n'
        "    expected_intent: FooIntent\n",
        encoding="utf-8",
    )
    assert lint_fixture_invocation_names(
        {LOCALE: {"invocationName": "model name"}}, fixtures_dir=tmp_path
    ) == []


def test_lint_smoke_open_without_the_key_is_clean(tmp_path):
    _write_fixture(tmp_path, f"e2e_smoke_{LOCALE}.yaml", LOCALE, None)
    assert lint_fixture_invocation_names(
        {LOCALE: {"invocationName": "model name"}}, fixtures_dir=tmp_path
    ) == []


def test_lint_padded_model_name_agrees_with_the_loader(tmp_path):
    # Review round 2, finding 5, the lint side: the composer sends the
    # stripped model name, so the exact header equality compares against
    # the stripped value too.
    warnings = lint_fixture_invocation_names(
        {LOCALE: {"invocationName": "  model name  "}}, fixtures_dir=tmp_path
    )
    assert _lint(tmp_path, "model name") == []
    assert warnings == []


def _committed_models() -> dict:
    """{locale: languageModel} for every committed model (read-only)."""
    models: dict = {}
    for path in sorted(DEFAULT_MODELS_DIR.glob("model_*.json")):
        locale = path.stem.removeprefix("model_")
        models[locale] = load_locale_model(locale)
    return models


def test_committed_smoke_opens_contain_their_model_names():
    # Real-data touch: the committed-tree check the validator performs;
    # all 17 smoke opens embed the current model invocationName.
    assert lint_fixture_invocation_names(
        _committed_models(), fixtures_dir=FIXTURES_DIR
    ) == []


# ------------------------------------------------- the wrapper battery


def _write_battery(path: Path, simulate_row: str, control: str | None) -> None:
    rows = [
        '        ("profile", "bare payload senza invocation", "SomeIntent"),',
        f'        ("simulate", "{simulate_row}", "SomeIntent"),',
    ]
    lines = ["BATTERY = {", '    "it-IT": [', *rows, "    ],", "}"]
    if control is not None:
        lines += ["EVERGREEN_CONTROLS = {", f'    "it-IT": "{control}",', "}"]
    else:
        lines.append("EVERGREEN_CONTROLS = {}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def test_battery_stale_simulate_row_and_control_warn(tmp_path):
    battery_path = tmp_path / "nlu_wrapper_battery.py"
    _write_battery(battery_path, "chiedi a vecchio nome di riproduci x", "chiedi a vecchio nome di controllo")
    warnings = lint_wrapper_battery_invocation(
        {"it-IT": {"invocationName": "mia collezione"}}, battery_path=battery_path
    )
    assert len(warnings) == 2
    assert all("does not contain the model invocationName 'mia collezione'" in w for w in warnings)
    assert any("simulate row" in w for w in warnings)
    assert any("evergreen control" in w for w in warnings)


def test_battery_matching_rows_are_clean(tmp_path):
    battery_path = tmp_path / "nlu_wrapper_battery.py"
    _write_battery(
        battery_path,
        "chiedi a mia collezione di riproduci x",
        "chiedi a mia collezione di controllo",
    )
    assert lint_wrapper_battery_invocation(
        {"it-IT": {"invocationName": "mia collezione"}}, battery_path=battery_path
    ) == []


def test_battery_profile_rows_are_never_checked(tmp_path):
    battery_path = tmp_path / "nlu_wrapper_battery.py"
    _write_battery(battery_path, "chiedi a mia collezione di x", None)
    # The profile row embeds a stale name on purpose: no warning, because a
    # profile row carries no invocation prefix by design.
    assert lint_wrapper_battery_invocation(
        {"it-IT": {"invocationName": "mia collezione"}}, battery_path=battery_path
    ) == []


def test_battery_locale_without_a_model_is_skipped(tmp_path):
    battery_path = tmp_path / "nlu_wrapper_battery.py"
    _write_battery(battery_path, "chiedi a chiunque di x", None)
    assert lint_wrapper_battery_invocation({}, battery_path=battery_path) == []


def test_committed_wrapper_battery_matches_the_models():
    # Real-data touch: the committed battery's it-IT and fr-FR simulate
    # rows and controls embed the current model invocationNames.
    assert lint_wrapper_battery_invocation(_committed_models()) == []
