# NLU Lab Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the workstation NLU lab (CLI `init-skill`/`run`/`export`/`compare` plus run JSON files and a phone-first read-only webapp) that iterates interaction models against a dedicated lab skill, per the approved spec at `docs/superpowers/specs/2026-11-21-nlu-lab-design.md`.

**Architecture:** A Python package under `scripts/nlu-lab/` with pure-logic modules (battery, runs, model sources, config guard) unit-tested with pytest, a thin SMAPI layer reusing the ask CLI and the repo's existing profile-nlu parsing, and a stdlib-http webapp that renders run files. Zero plugin changes. The acceptance bench is issue #46.

**Tech Stack:** Python 3 (stdlib and pytest), ask CLI v2 (`ask smapi`), the repo's own `validate_interaction_models.py`, `generate_interaction_model.py`, `nlu_wrapper_battery.py`, and `tests/integration/smapi_client.py`.

**Spec obligations baked into tasks:** the deploy guard (lab skill only, pinned name checked live), per-row `status` with error rows excluded from judgment, expectation snapshots, `intent_roster` snapshot, A/A noise control, `validate_single_model` import only, in-memory template build, export's required `--invocation-name`, runs/ and lab.json gitignored, CLI before webapp.

---

### Task 1: Scaffold and import-safety spike

**Files:**
- Create: `scripts/nlu-lab/__init__.py` (empty)
- Create: `scripts/nlu-lab/_paths.py`
- Create: `scripts/nlu-lab/tests/__init__.py` (empty)
- Test: `scripts/nlu-lab/tests/test_imports.py`

- [ ] **Step 1: Write the import-safety test**

```python
# scripts/nlu-lab/tests/test_imports.py
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
```

- [ ] **Step 2: Create `_paths.py` (the shared sys.path bootstrap)**

```python
# scripts/nlu-lab/_paths.py
"""sys.path bootstrap so the lab can import the repo's existing scripts."""
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

def bootstrap() -> None:
    for p in (REPO / "scripts", REPO / "tests" / "integration", Path(__file__).resolve().parent):
        s = str(p)
        if s not in sys.path:
            sys.path.insert(0, s)
```

- [ ] **Step 3: Run the tests.** This is the spike: if any module runs code at import or writes files, this FAILS and the executor must extract that module's needed functions into the lab with a documented divergence note in the commit message before continuing.

Run: `PYTEST_DISABLE_PLUGIN_AUTOLOAD=1 python3 -m pytest scripts/nlu-lab/tests/test_imports.py -v`
Expected: PASS. (The env prefix is REQUIRED on this workstation for every pytest run in this plan: the Home Assistant pytest plugin autoloads an autouse fixture that errors every test at collection, and at this spike its failure would mimic an import side effect and mislead the extraction path.)

- [ ] **Step 4: Commit**

```bash
git add scripts/nlu-lab/
git commit -m "nlu-lab: scaffold + import-safety spike over the reused repo modules"
```

### Task 2: lab.json config guard

**Files:**
- Create: `scripts/nlu-lab/lab_config.py`
- Test: `scripts/nlu-lab/tests/test_lab_config.py`

- [ ] **Step 1: Write the failing tests**

```python
# scripts/nlu-lab/tests/test_lab_config.py
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
```

- [ ] **Step 2: Run, expect failure** (`ModuleNotFoundError: lab_config`)

Run: `python3 -m pytest scripts/nlu-lab/tests/test_lab_config.py -v`. Expected: FAIL.

- [ ] **Step 3: Implement**

```python
# scripts/nlu-lab/lab_config.py
"""lab.json: the deploy guard's trust root (spec: missing/unparseable refuses, never re-inits;
id AND pinned name must both match, name checked against the LIVE skill at deploy time)."""
from dataclasses import dataclass
import json
from pathlib import Path

class LabConfigError(RuntimeError):
    pass

@dataclass(frozen=True)
class LabConfig:
    skill_id: str
    skill_name: str
    invocation_name: str

    @classmethod
    def load(cls, path: Path) -> "LabConfig":
        if not path.exists():
            raise LabConfigError(f"lab.json missing at {path}: the lab refuses to run rather than re-init; run init-skill once")
        try:
            data = json.loads(path.read_text())
        except json.JSONDecodeError as e:
            raise LabConfigError(f"lab.json unparseable ({e}): the lab refuses rather than re-init; fix or delete and re-run init-skill")
        if not data.get("skill_id") or not data.get("skill_name"):
            raise LabConfigError("lab.json must carry both skill_id and skill_name; refusing")
        if not data.get("invocation_name"):
            raise LabConfigError("lab.json must carry invocation_name (the dedicated lab invocation, substituted into every deployed model); refusing")
        return cls(skill_id=data["skill_id"], skill_name=data["skill_name"], invocation_name=data["invocation_name"])

    def guard_deploy_target(self, skill_id: str, live_skill_name: str | None = None) -> None:
        if skill_id != self.skill_id:
            raise LabConfigError(f"refusing to deploy to {skill_id}: not the lab skill")
        if live_skill_name is not None and live_skill_name != self.skill_name:
            raise LabConfigError(
                f"skill {skill_id} is now named '{live_skill_name}', not the pinned '{self.skill_name}'; "
                "a stale id pointing at a renamed or foreign skill is refused")

    @classmethod
    def save(cls, path: Path, skill_id: str, skill_name: str, invocation_name: str) -> None:
        path.write_text(json.dumps({"skill_id": skill_id, "skill_name": skill_name, "invocation_name": invocation_name}, indent=2))
```

- [ ] **Step 4: Run tests.** `PYTEST_DISABLE_PLUGIN_AUTOLOAD=1 python3 -m pytest scripts/nlu-lab/tests/test_lab_config.py -v`. Expected: 6 PASS.

- [ ] **Step 5: Commit.** `git add scripts/nlu-lab/ && git commit -m "nlu-lab: lab.json trust root and deploy guard"`

### Task 3: run schema (runs.py)

**Files:**
- Create: `scripts/nlu-lab/runs.py`
- Test: `scripts/nlu-lab/tests/test_runs.py`

- [ ] **Step 1: Write the failing tests**

```python
# scripts/nlu-lab/tests/test_runs.py
import json
from runs import RunRecord, RowResult, RowStatus, load_run, save_run

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
```

- [ ] **Step 2: Run, expect import failure.** `python3 -m pytest scripts/nlu-lab/tests/test_runs.py -v`. Expected: FAIL.

- [ ] **Step 3: Implement `runs.py`** with: `RowStatus` enum (OK, NO_SELECTION, ERROR); `RowResult` dataclass with exactly the schema fields (utterance, source, status, expected_intent, selected_intent, slots, considered_intents, raw); `RunRecord` dataclass (id, timestamp, locale, label, model_source, model_sha256, skill_id, status, error, intent_roster, results); `save_run(dir, run) -> Path` writing `{id}.json` atomically (tmp file plus rename); `load_run(path)` tolerant of unknown extra keys; expectation satisfaction splits `expected_intent` on `|` and treats MEMBERSHIP as satisfied (the fixtures carry pipe alternatives in every locale); `RunRecord.diff_against(old) -> list[RowDiff]` with `RowDiff = dataclass(utterance, source, kind)`, kind one of `same, green, red, amber, error, absent_old, absent_new`, keyed by `(utterance, source)`, computed ONLY from the two runs' own snapshots, error rows mapping to `error` regardless of change; and `RunRecord.expectation_failure_count` counting every non-ERROR row whose expectation is non-null and unsatisfied (NO_SELECTION with an expectation counts as a failure; only ERROR rows are excluded, per spec).

**Run all local pytest commands in this plan with `PYTEST_DISABLE_PLUGIN_AUTOLOAD=1` prefixed** (this workstation's Home Assistant pytest plugin autoloads an autouse fixture that errors every test at collection; a documented machine quirk. CI is unaffected.)

- [ ] **Step 4: Run tests.** Expected: PASS.

- [ ] **Step 5: Commit.** `git add scripts/nlu-lab && git commit -m "nlu-lab: run schema with snapshot judgment"`

### Task 4: battery assembly (battery.py)

**Files:**
- Create: `scripts/nlu-lab/battery.py`
- Test: `scripts/nlu-lab/tests/test_battery.py`
- Create: `scripts/nlu-lab/workingset-jf933.yaml`

- [ ] **Step 1: Write the failing tests** covering: fixture load for a locale EXCLUDES `e2e_*.yaml` (build a fake fixtures dir in tmp containing `de-DE.yaml` and `e2e_smoke_de-DE.yaml` and assert only the former's rows load); workingset load follows the fixture format with `expected_intent` optional; the `-k` substring filter applies to both sources; expectation resolution leaves workingset rows without `expected_intent` as None; duplicate utterances across sources stay distinct rows keyed by `(utterance, source)`.

- [ ] **Step 2: Run.** Expected: FAIL.

- [ ] **Step 3: Implement `battery.py`**: `load_battery(locale, fixtures_dir, workingset: Path | None, filter_k: str | None) -> list[BatteryRow]` using yaml safe_load, `BatteryRow = dataclass(utterance, source, expected_intent)` with source one of `fixture`, `workingset`.

- [ ] **Step 4: Run tests.** Expected: PASS.

- [ ] **Step 5: Write the JF-933 working set** (the reporter's table, ground truth from issue #46):

```yaml
# scripts/nlu-lab/workingset-jf933.yaml
locale: de-DE
tests:
  - utterance: "spiele das lied die streithörnchen"
    expected_intent: PlaySongIntent
  - utterance: "spiele den titel die streithörnchen"
    expected_intent: PlaySongIntent
  - utterance: "spiele den song die streithörnchen"
    expected_intent: PlaySongIntent
  - utterance: "spiele die streithörnchen"
  - utterance: "spiele musik von die streithörnchen"
    expected_intent: PlayArtistSongsIntent
  - utterance: "spiele das album magellan"
    expected_intent: PlayAlbumIntent
  - utterance: "spiele die playlist nico"
    expected_intent: PlayPlaylistIntent
```

(The bare-title row is an observation row: no expectation, judged amber only when it changes.)

- [ ] **Step 6: Commit.** `git add scripts/nlu-lab && git commit -m "nlu-lab: battery assembly + the jf933 working set"`

### Task 5: model sources (models.py)

**Files:**
- Create: `scripts/nlu-lab/models.py`
- Test: `scripts/nlu-lab/tests/test_models.py`

- [ ] **Step 1: Write the failing tests:** loading an external model file accepts both the SMAPI `{"interactionModel": ...}` wrapper and a bare languageModel dict, normalizing to the wrapper; `build_from_template(locale)` calls `generate_interaction_model.build_model` and `serialize_model` IN MEMORY and NEVER writes `model_*.json` (chdir to tmp, assert the directory stays empty after the call); `prepare_payload(model, lab_invocation_name)` returns `(payload, sha256, intent_roster)` where the payload's `interactionModel.languageModel.invocationName` is the lab name (never the source's), the sha256 covers the canonical serialized payload, and `intent_roster` lists every intent with its first sample.

- [ ] **Step 2: Run.** Expected: FAIL.

- [ ] **Step 3: Implement `models.py`**: `load_model_file(path) -> dict`, `build_from_template(locale) -> dict` (feeding `build_model` with the locale's template config loaded via the generator's own template-loading helper, `load_template_config(locale)`, never `main()`), `prepare_payload(model, lab_invocation_name) -> tuple[dict, str, list]`.

- [ ] **Step 4: Run tests.** Expected: PASS.

- [ ] **Step 5: Commit.** `git add scripts/nlu-lab && git commit -m "nlu-lab: model sources with in-memory template build and lab-name substitution"`

### Task 6: SMAPI operations (smapi_ops.py)

**Files:**
- Create: `scripts/nlu-lab/smapi_ops.py`
- Test: `scripts/nlu-lab/tests/test_smapi_ops.py`

- [ ] **Step 1: Write the failing tests** (pure-parse level, no network): `parse_skill_name` extracts the name from a SAMPLE `ask smapi get-skill-manifest` payload (pin the sample in the test: `{"manifest": {"publishingInformation": {"locales": {"en-US": {"name": "jellyfin nlu lab"}}}}}`; note the installed ask CLI has NO `get-skill` subcommand, `get-skill-manifest` is the live-name source); `parse_build_status` maps a SAMPLE `get-skill-status` payload to SUCCEEDED, FAILED, or IN_PROGRESS, pinning the SMAPI v1 per-resource shape the repo's own SmapiManagement.WaitForSkillStatusAsync consumes: `{"body": {"interactionModel": {"lastModified": {"status": "SUCCEEDED", "locale": "de-DE"}}}}` (tolerate the outer `body` wrapper being present or absent, and an absent locale meaning IN_PROGRESS; a WRONG pinned shape here would pass its unit test while wait_for_build burns the full 600s timeout on every live deploy, so at Task 12 Step 2 the first live build-wait also dumps the raw payload once and the executor eyeballs parse_build_status against it before freezing); `run_battery_rows(profile_fn, rows, delay)` with a recording fake profile_fn spaces calls by the delay, maps a missing `selectedIntent` to NO_SELECTION, maps an exception to ERROR after exactly one retry (the JF-406 single-500 class), and never lets one error row abort the loop.

- [ ] **Step 2: Run.** Expected: FAIL.

- [ ] **Step 3: Implement `smapi_ops.py`**, reusing the repo's ask-CLI runner from `nlu_wrapper_battery` (`_run_ask`, `SMAPI_DELAY` with default 1.5) and the profile payload PARSING from `tests/integration/smapi_client.py` (reuse `SmapiClient.parse_profile_nlu_result` for slots and considered intents, wrapping it in the thin catch the NO_SELECTION mapping already implies since it raises `SmapiError` on the no-selectedIntent shape; `nlu_wrapper_battery.profile_nlu` returns only the intent name and misses them): `get_skill_name(skill_id)` via `ask smapi get-skill-manifest --skill-id ... --stage development`, parsing the en-US (or first) publishing locale's name; `deploy_model(skill_id, locale, payload)` writing the payload to a tmp file and invoking `ask smapi set-interaction-model --skill-id ... --stage development --locale ... --interaction-model file:`; `wait_for_build(skill_id, locale, timeout_s=600)` polling `ask smapi get-skill-status` every 10 seconds; `profile_row(skill_id, locale, utterance)`; `run_battery_rows(...)` as tested.

- [ ] **Step 4: Run tests.** Expected: PASS.

- [ ] **Step 5: Commit.** `git add scripts/nlu-lab && git commit -m "nlu-lab: smapi operations (deploy, build wait, battery runner with JF-406 retry)"`

### Task 7: the run command (cli.py)

**Files:**
- Create: `scripts/nlu-lab/cli.py`
- Test: `scripts/nlu-lab/tests/test_cli_run.py`

- [ ] **Step 1: Write the failing test** (orchestration with smapi_ops and models monkeypatched): `cmd_run` loads lab.json (skill_id, skill_name, invocation_name), guards the target with the LIVE name check, resolves the model source (`--model` or `--template`), preflights via `validate_single_model(locale, lm)` refusing on any structural error (feed a model dict that triggers one and assert refusal before any deploy), substitutes the lab invocation name FROM lab.json's invocation_name, deploys, waits for the build, runs the battery snapshotting expectations, and saves the run file; on a monkeypatched deploy exception it saves a `failed` run; on KeyboardInterrupt mid-battery it saves the partial run with status `interrupted` (the run file exists from run start, per spec).

- [ ] **Step 2: Run.** Expected: FAIL.

- [ ] **Step 3: Implement `cmd_run`** with argparse wiring: `run --locale de-DE [--model PATH | --template] --label L [--workingset PATH] [-k SUBSTR]`.

- [ ] **Step 4: Run tests.** Expected: PASS.

- [ ] **Step 5: Commit.** `git add scripts/nlu-lab && git commit -m "nlu-lab: the run command end to end"`

### Task 8: init-skill

**Files:**
- Modify: `scripts/nlu-lab/cli.py`
- Test: `scripts/nlu-lab/tests/test_cli_init.py`

- [ ] **Step 1: Write the failing tests:** `prepare_manifest(endpoint_uri, events_uri, version, skill_name)` reads the committed `Jellyfin.Plugin.AlexaSkill/Alexa/Manifest/manifest.json`, INSERTS `manifest.apis.custom.endpoint.uri` (absent in the template), fills `manifest.events.endpoint.uri`, stamps the version into `testingInstructions`, STAMPS skill_name into every `publishingInformation.locales.*.name` (otherwise the created skill's live name stays the template's "Jellyfin" and the guard's live-name check would refuse every deploy), and keeps all 17 publishing locales; `cmd_init --name N --invocation I --endpoint URI` with a monkeypatched ask runner creates the skill and writes lab.json carrying id, name, and invocation_name; re-running without `--force` refuses to overwrite lab.json.

- [ ] **Step 2: Run.** Expected: FAIL.

- [ ] **Step 3: Implement:** `prepare_manifest(...)` plus `cmd_init --name N --invocation I --endpoint URI`, creating via `ask smapi create-skill-for-vendor --manifest file:` (the CLI resolves the vendor implicitly from its own state; pass `--vendor-id` explicitly if the installed CLI requires it). If the installed ask CLI lacks that subcommand, `cmd_init` prints the developer-console fallback (create a custom skill with the pinned name, then run `init-skill --pin-id <id>`) and exits 2; `--pin-id` verifies the LIVE name via `get-skill-manifest` matches before recording id, name, and invocation_name into lab.json.

- [ ] **Step 4: Run tests.** Expected: PASS.

- [ ] **Step 5: Commit.** `git add scripts/nlu-lab && git commit -m "nlu-lab: init-skill with manifest preparation and console fallback"`

### Task 9: export and compare

**Files:**
- Modify: `scripts/nlu-lab/cli.py`
- Test: `scripts/nlu-lab/tests/test_cli_export_compare.py`

- [ ] **Step 1: Write the failing tests:** `cmd_export` requires `--invocation-name` and refuses without it (the spec's rename hazard: the plugin's custom-model import deploys the file's invocationName verbatim), writes the deploy-ready payload carrying exactly that name (never the source's, never the lab's) to the RECOMMENDED filename `{locale}-{label}.json`, and preflights with `validate_single_model`; `cmd_compare RUN_A RUN_B` prints the changed rows from `diff_against` and with `--expect-zero-changes` exits nonzero when anything is not `same` (the A/A gate).

- [ ] **Step 2: Run.** Expected: FAIL.

- [ ] **Step 3: Implement both commands.**

- [ ] **Step 4: Run tests.** Expected: PASS.

- [ ] **Step 5: Commit.** `git add scripts/nlu-lab && git commit -m "nlu-lab: export with mandatory invocation name + compare with the A/A gate"`

### Task 10: gitignore and CI wiring

**Files:**
- Modify: `.gitignore`
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1:** append to `.gitignore`: `scripts/nlu-lab/runs/`, `scripts/nlu-lab/lab.json`, and `scripts/nlu-lab/tests/__screenshots__/`.

- [ ] **Step 2:** add a `lab-tests` step to the ci.yml validate job (after validate-models) running `python3 -m pytest scripts/nlu-lab/tests -q`; it needs no SMAPI credentials (all unit level, smapi monkeypatched) and fails the job on failure.

- [ ] **Step 3:** run `python3 -m pytest scripts/nlu-lab/tests -q` locally. Expected: all PASS.

- [ ] **Step 4: Commit.** `git add .gitignore .github/workflows/ci.yml scripts/nlu-lab && git commit -m "nlu-lab: gitignore runs+config+screenshots, CI lab-tests step"`

### Task 11: webapp (serve)

**Files:**
- Create: `scripts/nlu-lab/webapp.py`
- Create: `scripts/nlu-lab/web/index.html`, `scripts/nlu-lab/web/app.js`, `scripts/nlu-lab/web/strings.en.json`, `scripts/nlu-lab/web/strings.it.json`
- Test: `scripts/nlu-lab/tests/test_webapp.py`

- [ ] **Step 1: Write the failing tests:** `build_page_state(runs_dir, locale)` returns the run list (label, timestamp, expectation-failure count per run), the matrix rows for two chosen runs (union keyed by utterance plus source, kinds from `diff_against`), and the legend from the newer run's `intent_roster`; the strings loader picks `it` when requested and falls back to `en` key-by-key; the HTTP handler serves only files under `web/` and the JSON state endpoint, refusing path traversal.

- [ ] **Step 2: Run.** Expected: FAIL.

- [ ] **Step 3: Implement:** stdlib `http.server` on `0.0.0.0` (port flag, default 8765), a phone-first single page following the approved mockup: run selectors, the matrix with red/amber/error/absent cells, row detail with slots and considered intents, the collapsible intent legend, and a language toggle; chrome strings from the strings files, probe content untouched.

- [ ] **Step 4: Run tests.** Expected: PASS.

- [ ] **Step 5: Rendered verification (house rule):** start `cli.py serve` against a tmp runs dir with two synthetic runs, open it via the Chrome DevTools MCP at a 390px phone viewport, and screenshot the matrix showing a red, an amber, and an error cell with the legend open; save under `scripts/nlu-lab/tests/__screenshots__/` (gitignored) and confirm legibility at that width before committing.

- [ ] **Step 6: Commit.** `git add scripts/nlu-lab && git commit -m "nlu-lab: phone-first read-only webapp"`

### Task 12: acceptance bench, live (issue #46)

**Files:** none created; outcomes land in the JF-933 task notes.

- [ ] **Step 1:** run `init-skill` (console fallback allowed; endpoint = the minix plugin URL, harmless since profile-nlu never invokes it).

- [ ] **Step 2: A/A gate.** `run --template --locale de-DE --label stock-aa-1 --workingset scripts/nlu-lab/workingset-jf933.yaml -k streithörnchen`, then the same again as `stock-aa-2`. On the FIRST build-wait, dump the raw `get-skill-status` payload once and eyeball `parse_build_status` against it before freezing (the pinned-sample drift guard from Task 6). Then `compare stock-aa-1 stock-aa-2 --expect-zero-changes` must pass. Note the `-k` filter covers 5 of the 7 working-set rows (the album and playlist rows lack the substring and are excluded from this fast pass; the full battery in Step 3 covers everything). Any change means noise: the bench stops and the noise is investigated before anything else.

- [ ] **Step 3: stock reproduction.** Full `run --template --locale de-DE --label stock-base --workingset workingset-jf933.yaml`; the matrix must record the reporter's table (das Lied to MediaInfo, den Titel and den Song to PlayRadio, the bare title no_selection, musik von to PlayArtistSongs, album and playlist OK). Any divergence from the reporter's observed results is cross-checked with him (his standing offer) before trusting either side.

- [ ] **Step 4:** the JF-933 fix candidate through the matrix is JF-933's own execution work, not this plan's; this plan ends at the validated baseline.

- [ ] **Step 5:** record the bench results in the JF-933 backlog task notes.

---

## Verification summary

`python3 -m pytest scripts/nlu-lab/tests -q` green locally and in the new CI step; the acceptance bench's A/A gate and stock reproduction green live; no plugin file touched (`git diff --stat Jellyfin.Plugin.AlexaSkill` empty across the whole plan).
