"""NLU Lab CLI. Today one subcommand: `run` (deploy a model to the dedicated lab
skill, wait for the build, profile the battery, snapshot a run file).

Orchestration contract (spec): preflight refusals happen BEFORE any run file
exists and before any deploy; the run file is created EARLY in the "running"
state so a mid-run crash or Ctrl-C still leaves an artifact, then rewritten to
ok / failed / interrupted. Every smapi_ops and models call goes through the
module attribute (smapi_ops.deploy_model, ...) so tests monkeypatch the module,
never this file's namespace.
"""
import argparse
import re
import sys
from datetime import datetime
from pathlib import Path

import _paths
_paths.bootstrap()

import yaml

import battery
import models
import smapi_ops
from lab_config import LabConfig, LabConfigError
from runs import RowResult, RunRecord, save_run
from validate_interaction_models import validate_single_model

LAB_DIR_DEFAULT = Path(__file__).resolve().parent
FIXTURES_DIR_DEFAULT = _paths.REPO / "tests" / "integration" / "fixtures"

STATUS_RUNNING = "running"
STATUS_OK = "ok"
STATUS_FAILED = "failed"
STATUS_INTERRUPTED = "interrupted"


class RunRefused(RuntimeError):
    """A preflight refusal: nothing was deployed and no run file was created."""


def _slug(label: str) -> str:
    s = re.sub(r"[^a-z0-9]+", "-", label.lower()).strip("-")
    return s or "run"


def _language_model(model: dict, source: str) -> dict:
    """The languageModel dict from either model shape (bare build_model dict or
    the interactionModel wrapper), for validate_single_model."""
    lm = None
    if isinstance(model, dict):
        if "interactionModel" in model:
            lm = model["interactionModel"].get("languageModel")
        elif "languageModel" in model:
            lm = model["languageModel"]
    if not isinstance(lm, dict):
        raise RunRefused(
            f"{source}: neither an interactionModel wrapper nor a languageModel "
            "dict; not a model the lab can deploy")
    return lm


def _resolve_model(args) -> tuple[dict, str]:
    if args.template:
        try:
            model = models.build_from_template(args.locale)
        except Exception as exc:  # noqa: BLE001 - refuse cleanly, never a traceback
            raise RunRefused(f"template build for {args.locale} failed: {exc}") from exc
        return model, f"template:{args.locale}"
    path = Path(args.model)
    if not path.is_file():
        raise RunRefused(f"--model {path}: no such file")
    try:
        model = models.load_model_file(path)
    except (ValueError, OSError) as exc:
        raise RunRefused(f"--model {path}: {exc}") from exc
    return model, str(path)


def _check_workingset(path: Path, locale: str) -> None:
    """Refuse a workingset whose locale differs from the run locale: load_battery
    never reads that field, so this check lives here (Task 4 review mandate)."""
    try:
        data = yaml.safe_load(path.read_text(encoding="utf-8"))
    except yaml.YAMLError as exc:
        raise RunRefused(f"workingset {path} is not parseable YAML: {exc}") from exc
    if not isinstance(data, dict):
        raise RunRefused(
            f"workingset {path}: top level is {type(data).__name__}, expected a mapping")
    ws_locale = data.get("locale")
    if ws_locale != locale:
        raise RunRefused(
            f"workingset {path} declares locale {ws_locale!r} but the run locale is "
            f"{locale!r}; refusing to feed another locale's rows to profile-nlu")


def cmd_run(args, *, lab_dir: Path | None = None,
            fixtures_dir: Path | None = None) -> int:
    lab_dir = Path(lab_dir) if lab_dir is not None else LAB_DIR_DEFAULT
    fixtures = Path(fixtures_dir) if fixtures_dir is not None else FIXTURES_DIR_DEFAULT
    try:
        run, rows, payload = _preflight(args, lab_dir, fixtures)
    except (RunRefused, LabConfigError) as exc:
        print(f"refusing to run: {exc}", file=sys.stderr)
        return 1

    runs_dir = lab_dir / "runs"
    run_path = save_run(runs_dir, run)  # exists from run start, in "running" state
    print(f"run {run.id} started -> {run_path}")

    raws: dict[str, dict] = {}

    def profile_fn(utterance: str) -> dict:
        response = smapi_ops.profile_row(run.skill_id, args.locale, utterance)
        raws[utterance] = response
        return response

    try:
        print(f"deploying {args.locale} model from {run.model_source} "
              f"(sha {run.model_sha256[:12]}) to skill {run.skill_id}")
        smapi_ops.deploy_model(run.skill_id, args.locale, payload)
        print("deployed; waiting for the model build")
        if not smapi_ops.wait_for_build(run.skill_id, args.locale):
            raise RuntimeError(f"model build for {args.locale} did not succeed before the timeout")
        print(f"build succeeded; profiling {len(rows)} battery rows")
        row_dicts = smapi_ops.run_battery_rows(profile_fn, rows)
        run.results = [_to_row_result(r, d, raws) for r, d in zip(rows, row_dicts)]
        run.status = STATUS_OK
    except KeyboardInterrupt:
        run.status = STATUS_INTERRUPTED
        done = [r for r in rows if r.utterance in raws]
        if done:
            # Replay the already-fetched responses through run_battery_rows (a pure
            # dict-lookup profile_fn, delay 0): the partial rows get the exact same
            # parse/status logic as the live path, with no second network call.
            replay = smapi_ops.run_battery_rows(lambda u: raws[u], done, delay=0.0)
            run.results = [_to_row_result(r, d, raws) for r, d in zip(done, replay)]
        run.error = f"interrupted after {len(run.results)} of {len(rows)} rows"
    except Exception as exc:  # noqa: BLE001 - any live-phase failure becomes a failed run
        run.status = STATUS_FAILED
        run.error = f"{type(exc).__name__}: {exc}"

    save_run(runs_dir, run)
    if run.status == STATUS_OK:
        print(f"run ok: {len(run.results)} rows, "
              f"expectation failures: {run.expectation_failure_count} -> {run_path}")
        return 0
    if run.status == STATUS_INTERRUPTED:
        print(f"run interrupted: {run.error}; partial run saved -> {run_path}",
              file=sys.stderr)
        return 130
    print(f"run failed: {run.error} -> {run_path}", file=sys.stderr)
    return 1


def _to_row_result(row, row_dict: dict, raws: dict) -> RowResult:
    return RowResult(
        utterance=row.utterance,
        source=row.source,
        status=row_dict["status"],
        expected_intent=row.expected_intent,
        selected_intent=row_dict["selected_intent"],
        slots=row_dict["slots"],
        considered_intents=row_dict["considered_intents"],
        raw=raws.get(row.utterance) or {},
    )


def _preflight(args, lab_dir: Path, fixtures: Path):
    """Everything that can refuse without a deploy: config load, the LIVE name
    guard, model source resolution, structural validation, battery assembly.
    Returns the fully-populated (status=running) RunRecord, the battery rows,
    and the deploy payload."""
    cfg = LabConfig.load(lab_dir / "lab.json")

    try:
        live_name = smapi_ops.get_skill_name(cfg.skill_id)
    except Exception as exc:  # noqa: BLE001 - a failed live read is a refusal, not a traceback
        raise RunRefused(
            f"could not read the live name of skill {cfg.skill_id}: {exc}") from exc
    cfg.guard_deploy_target(cfg.skill_id, live_skill_name=live_name)

    model, model_source = _resolve_model(args)
    lm = _language_model(model, model_source)
    errors, warnings = validate_single_model(args.locale, lm)
    for w in warnings:
        print(f"warning: {w}", file=sys.stderr)
    if errors:
        raise RunRefused("model preflight found structural errors:\n" + "\n".join(errors))

    workingset = None
    if args.workingset is not None:
        workingset = Path(args.workingset)
        if not workingset.is_file():
            raise RunRefused(f"--workingset {workingset}: no such file")
        _check_workingset(workingset, args.locale)

    rows = battery.load_battery(args.locale, fixtures, workingset, args.filter_k)
    for i, row in enumerate(rows):
        if not isinstance(row.utterance, str) or not row.utterance.strip():
            raise RunRefused(
                f"battery row #{i + 1} (source '{row.source}', expected_intent "
                f"{row.expected_intent!r}) carries a None/empty utterance; fix the "
                "fixture or workingset before it reaches profile-nlu")
    if not rows:
        raise RunRefused(
            f"battery assembled 0 rows for {args.locale} (fixtures {fixtures}, "
            f"workingset {workingset}); refusing to deploy for an empty run")

    payload, sha, roster = models.prepare_payload(model, cfg.invocation_name)

    now = datetime.now()
    run = RunRecord(
        id=f"{now:%Y%m%d-%H%M%S}-{_slug(args.label)}",
        timestamp=now.isoformat(timespec="seconds"),
        locale=args.locale,
        label=args.label,
        model_source=model_source,
        model_sha256=sha,
        skill_id=cfg.skill_id,
        status=STATUS_RUNNING,
        intent_roster=roster,
    )
    return run, rows, payload


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="nlu-lab",
        description="Deploy-and-probe harness for interaction models against a dedicated lab skill")
    sub = parser.add_subparsers(dest="command", required=True)
    run = sub.add_parser("run", help="deploy a model to the lab skill and run the battery")
    run.add_argument("--locale", required=True)
    source = run.add_mutually_exclusive_group(required=True)
    source.add_argument("--model", type=Path, default=None,
                        help="path to a model JSON file (interactionModel wrapper or bare languageModel)")
    source.add_argument("--template", action="store_true",
                        help="build the locale's model in memory from its YAML template")
    run.add_argument("--label", required=True, help="run label; slug becomes part of the run id")
    run.add_argument("--workingset", type=Path, default=None,
                     help="optional workingset YAML in the fixture format (its locale must match)")
    run.add_argument("-k", dest="filter_k", default=None, metavar="SUBSTR",
                     help="substring filter on utterances, applied across both battery sources")
    run.set_defaults(func=cmd_run)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
