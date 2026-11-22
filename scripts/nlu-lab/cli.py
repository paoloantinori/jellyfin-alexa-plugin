"""NLU Lab CLI. Five subcommands: `init-skill` (create or pin the dedicated lab
skill and write lab.json), `run` (deploy a model to that skill, wait for the
build, profile the battery, snapshot a run file), `export` (write a deploy-ready
payload stamped with the caller's explicit invocation name), `compare`
(diff two run files; --expect-zero-changes is the A/A gate), and `serve`
(the read-only phone-first diff webapp over runs/).

Orchestration contract (spec): preflight refusals happen BEFORE any run file
exists and before any deploy; the run file is created EARLY in the "running"
state so a mid-run crash or Ctrl-C still leaves an artifact, then rewritten to
ok / failed / interrupted. init-skill refuses to overwrite an existing lab.json
before any ask call fires. Every smapi_ops and models call goes through the
module attribute (smapi_ops.deploy_model, ...) so tests monkeypatch the module,
never this file's namespace.
"""
import argparse
import json
import re
import sys
from datetime import datetime
from pathlib import Path

import _paths
_paths.bootstrap()

import battery
import models
import smapi_ops
import webapp
from lab_config import LabConfig, LabConfigError
from runs import RowResult, RunLoadError, RunRecord, load_run, save_run
from smapi_client import SmapiError
from validate_interaction_models import validate_single_model
from validate_versions import get_props_version

LAB_DIR_DEFAULT = Path(__file__).resolve().parent
FIXTURES_DIR_DEFAULT = _paths.REPO / "tests" / "integration" / "fixtures"
MANIFEST_TEMPLATE_DEFAULT = (
    _paths.REPO / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "Manifest" / "manifest.json")

DEFAULT_SKILL_NAME = "jellyfin nlu lab"
DEFAULT_INVOCATION = "jellyfin nlu laboratorio"

STATUS_RUNNING = "running"
STATUS_OK = "ok"
STATUS_FAILED = "failed"
STATUS_INTERRUPTED = "interrupted"


class RunRefused(RuntimeError):
    """A preflight refusal: nothing was deployed and no run file was created."""


class InitRefused(RuntimeError):
    """An init-skill refusal: no skill was created and lab.json was not touched."""


def _lab_dir_from(args, lab_dir: Path | None) -> Path:
    """Resolve the lab dir: explicit kwarg (tests) > the --lab-dir flag > the
    production default scripts/nlu-lab/. The flag keeps the redirect visible
    on the command line (an env var would silently redirect every later lab
    command from a stale shell)."""
    if lab_dir is not None:
        return Path(lab_dir)
    flagged = getattr(args, "lab_dir", None)
    return Path(flagged) if flagged else LAB_DIR_DEFAULT


def _slug(label: str) -> str:
    s = re.sub(r"[^a-z0-9]+", "-", label.lower()).strip("-")
    return s or "run"


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


def _validated_payload(model: dict, model_source: str, invocation_name: str,
                       locale: str) -> tuple[dict, str, list]:
    """The one prepare-and-validate sequence both deploy paths ride (run and
    export), so they can never validate different shapes: stamp the payload
    with the caller's invocation name FIRST, then run structural validation on
    the POST-substitution languageModel (the exact model being deployed; a
    name-less external source is not a structural error because the name is
    always substituted). Returns (payload, sha256, intent_roster); warnings
    print, errors refuse."""
    try:
        payload, sha, roster = models.prepare_payload(model, invocation_name)
    except ValueError as exc:
        raise RunRefused(f"{model_source}: {exc}") from exc
    errors, warnings = validate_single_model(
        locale, payload["interactionModel"]["languageModel"])
    for w in warnings:
        print(f"warning: {w}", file=sys.stderr)
    if errors:
        raise RunRefused("model preflight found structural errors:\n" + "\n".join(errors))
    return payload, sha, roster


def prepare_manifest(endpoint_uri: str, version: str, skill_name: str,
                     template_path: Path | None = None) -> dict:
    """Read the committed skill-manifest template and return a creation payload
    stamped for the lab; the template on disk is never modified.

    - INSERTS manifest.apis.custom.endpoint.uri (absent from the template)
    - REMOVES the whole manifest.events block and the empty icon URI
      properties: SMAPI skill CREATION (live 400 evidence, 2026-11-21) rejects
      the template's events subscriptions (AMAZON.MediaContent.Available is
      not an allowed SUBSCRIPTION enum value at creation; the plugin's own
      skill predates that validation) and rejects empty smallIconUri/
      largeIconUri as unexpected properties. The lab skill needs neither
      proactive events nor icons: profile-nlu never invokes the endpoint.
    - stamps the version into publishingInformation.testingInstructions
    - stamps skill_name into EVERY publishingInformation.locales.*.name: the
      run command reads the live en-US name at deploy time and refuses a
      mismatch, so a locale left at the template's "Jellyfin" would make every
      later deploy refuse
    """
    path = Path(template_path) if template_path is not None else MANIFEST_TEMPLATE_DEFAULT
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise InitRefused(f"manifest template {path} unreadable: {exc}") from exc
    manifest = doc.get("manifest") if isinstance(doc, dict) else None
    custom = ((manifest or {}).get("apis") or {}).get("custom")
    publishing = (manifest or {}).get("publishingInformation")
    locales = (publishing or {}).get("locales")
    # One up-front shape check (template is repo-committed; this catches a
    # hand-mangled or wrong-file template), then the stamps below are plain.
    if not (isinstance(manifest, dict) and isinstance(custom, dict) and custom
            and isinstance(publishing, dict)
            and isinstance(locales, dict) and locales
            and all(isinstance(entry, dict) for entry in locales.values())):
        raise InitRefused(
            f"{path}: not the expected skill manifest shape (manifest.apis.custom, "
            "publishingInformation.locales of mappings); "
            "refusing")
    custom["endpoint"] = {"uri": endpoint_uri}
    manifest.pop("events", None)
    for icon_key in ("smallIconUri", "largeIconUri"):
        if not publishing.get(icon_key):
            publishing.pop(icon_key, None)
    instructions = publishing.get("testingInstructions")
    original = instructions if isinstance(instructions, str) else ""
    publishing["testingInstructions"] = f"nlu-lab {version}. {original}".strip()
    for entry in locales.values():
        entry["name"] = skill_name
    return doc


def _plugin_version() -> str:
    version = get_props_version()
    if not version:
        raise InitRefused("could not read the plugin version from Directory.Build.props")
    return version


def cmd_init(args, *, lab_dir: Path | None = None) -> int:
    config_path = _lab_dir_from(args, lab_dir) / "lab.json"
    try:
        if config_path.exists() and not args.force:
            raise InitRefused(
                f"{config_path} already exists (pass --force to re-init); "
                "no skill was created")
        if args.pin_id:
            return _init_from_pin(args, config_path)
        return _init_by_create(args, config_path)
    except (InitRefused, SmapiError, OSError) as exc:
        print(f"refusing to init: {exc}", file=sys.stderr)
        return 1


def _init_by_create(args, config_path: Path) -> int:
    if not args.endpoint:
        raise InitRefused(
            "--endpoint is required when creating the lab skill (the HTTPS URI "
            "Alexa posts skill requests to)")
    if not smapi_ops.has_create_skill_command():
        print(
            "this ask CLI has no `smapi create-skill-for-vendor` subcommand, so the\n"
            "lab skill must be created in the developer console:\n"
            f"  1. create a custom skill named exactly '{args.name}'\n"
            "  2. copy its skill id (amzn1.ask.skill....)\n"
            "  3. run: init-skill --pin-id <that-id>\n"
            "     (if you named it differently, pass --name '<its exact name>')")
        return 2
    payload = prepare_manifest(endpoint_uri=args.endpoint,
                               version=_plugin_version(), skill_name=args.name)
    skill_id = smapi_ops.create_skill(payload)
    LabConfig.save(config_path, skill_id, args.name, args.invocation)
    print(f"lab skill '{args.name}' created ({skill_id}); lab.json -> {config_path}")
    return 0


def _init_from_pin(args, config_path: Path) -> int:
    try:
        live_name = smapi_ops.get_skill_name(args.pin_id)
    except Exception as exc:  # noqa: BLE001 - a failed live read is a refusal, not a traceback
        raise InitRefused(
            f"could not read the live name of skill {args.pin_id}: {exc}") from exc
    if live_name != args.name:
        raise InitRefused(
            f"skill {args.pin_id} is named '{live_name}' on the console, not "
            f"'{args.name}'; re-run with --name set to the console skill's exact "
            "name (the deploy-time live-name guard compares against the pinned name)")
    LabConfig.save(config_path, args.pin_id, args.name, args.invocation)
    print(f"lab skill '{args.name}' pinned ({args.pin_id}); lab.json -> {config_path}")
    return 0


def cmd_run(args, *, lab_dir: Path | None = None,
            fixtures_dir: Path | None = None) -> int:
    lab_dir = _lab_dir_from(args, lab_dir)
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
        if args.skip_deploy:
            print(f"skip-deploy: profiling the ALREADY-deployed model (sha {run.model_sha256[:12]}) "
                  "for the A/A noise control")
            try:
                live = smapi_ops.get_build_entry(run.skill_id, args.locale)
                if live is not None:
                    run.build_etag = live.get("eTag")
            except smapi_ops.SmapiError:
                pass  # degraded identity: the sha guard still applies
        else:
            print(f"deploying {args.locale} model from {run.model_source} "
                  f"(sha {run.model_sha256[:12]}) to skill {run.skill_id}")
            try:
                baseline = smapi_ops.get_build_entry(run.skill_id, args.locale)  # JF-934 witness
            except smapi_ops.SmapiError as exc:
                baseline = None
                print(f"WARNING: pre-deploy build status unreadable ({exc}); the stale-SUCCEEDED "
                      "guard is DEGRADED for this run (a previous build's success could satisfy "
                      "the wait)", file=sys.stderr)
            # an ABSENT entry (fresh skill, nothing deployed yet) needs no warning: baseline stays None silently
            smapi_ops.deploy_model(run.skill_id, args.locale, payload)
            print("deployed; waiting for the model build")
            if not smapi_ops.wait_for_build(run.skill_id, args.locale, baseline=baseline):
                raise RuntimeError(f"model build for {args.locale} did not succeed before the timeout")
            try:
                fresh = smapi_ops.get_build_entry(run.skill_id, args.locale)
                if fresh is not None:
                    run.build_etag = fresh.get("eTag")
            except smapi_ops.SmapiError:
                pass  # degraded identity: the sha guard still applies
        print(f"profiling {len(rows)} battery rows")
        row_dicts = smapi_ops.run_battery_rows(profile_fn, rows)
        run.results = [_to_row_result(r, d, raws) for r, d in zip(rows, row_dicts)]
        run.status = STATUS_OK
        # JF-941: bracket the battery. If the build eTag changed between the
        # pre-battery capture and now, a concurrent redeploy mixed two models
        # in this run: mark it tainted so the compare gate can refuse it.
        if run.build_etag:
            try:
                post = smapi_ops.get_build_entry(run.skill_id, args.locale)
                if post is not None and post.get("eTag") != run.build_etag:
                    run.status = "tainted"
                    run.error = (f"build changed mid-battery (eTag {run.build_etag} -> "
                                 f"{post.get('eTag')}): routing results may mix two models")
                    print(f"WARNING: {run.error}", file=sys.stderr)
            except smapi_ops.SmapiError:
                pass  # degraded bracket: the sha guard still applies
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
    if run.status == "tainted":
        print(f"run TAINTED: {run.error}; results saved but untrustworthy -> {run_path}",
              file=sys.stderr)
        return 1
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
    payload, sha, roster = _validated_payload(model, model_source,
                                              cfg.invocation_name, args.locale)

    workingset = Path(args.workingset) if args.workingset is not None else None
    try:
        rows = battery.load_battery(args.locale, fixtures, workingset, args.filter_k)
    except battery.WorkingsetRefused as exc:
        raise RunRefused(str(exc)) from exc
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


def cmd_export(args) -> int:
    """Write a deploy-ready SMAPI payload carrying the CALLER-NAMED invocation
    name, never the source model's and never the lab's: the plugin's
    custom-model import deploys the file's invocationName verbatim, so an
    exported file with a guessed name would rename the importing skill.
    Fully offline: no lab.json is read, nothing is deployed. Refuses (exit 1,
    no file written) on a blank name, an unusable model source, or validator
    errors; warnings print but do not block."""
    try:
        name = args.invocation_name
        if not name or not name.strip():
            raise RunRefused(
                "--invocation-name is required: the exported file's invocationName "
                "is what the plugin's custom-model import deploys verbatim, so the "
                "lab refuses to guess (never the source model's name, never the lab's)")
        model, model_source = _resolve_model(args)
        payload, _, _ = _validated_payload(model, model_source, name, args.locale)
        out_dir = Path(args.out) if args.out else Path.cwd()
        out_dir.mkdir(parents=True, exist_ok=True)
        path = out_dir / f"{args.locale}-{_slug(args.label)}.json"
        path.write_text(json.dumps(payload, indent=2, ensure_ascii=False), encoding="utf-8")
    except (RunRefused, OSError) as exc:
        print(f"refusing to export: {exc}", file=sys.stderr)
        return 1
    print(f"exported {args.locale} model from {model_source} -> {path} "
          f"(invocation name: {name!r})")
    return 0


def _resolve_run_id(runs_dir: Path, run_id: str) -> str:
    """JF-943: users think in labels ('stock-base'), not timestamped stems
    ('20261121-183426-stock-base'). Resolution order: exact stem match, then
    the NEWEST run whose label equals the argument, then the newest run whose
    stem CONTAINS the argument. Returns the resolved stem."""
    # 1. Exact stem match (the current behavior, always wins)
    if (runs_dir / f"{run_id}.json").is_file():
        return run_id
    # 2. Label match: scan all runs, find the newest whose label equals
    candidates: list[tuple[str, str]] = []  # (timestamp_prefix, stem)
    for p in sorted(runs_dir.glob("*.json"), reverse=True):
        try:
            rec = load_run(p)
        except RunLoadError:
            continue
        if rec.label == run_id:
            return p.stem
        # 3. Stem-contains match (partial label or partial stem)
        if run_id in p.stem:
            candidates.append((p.stem[:15], p.stem))
    if candidates:
        return max(candidates)[1]
    return run_id  # fall through: _load_run_or_refuse will produce the error


def _load_run_or_refuse(runs_dir: Path, run_id: str) -> RunRecord:
    """Thin wrapper: the corrupt-run catch lives in runs.load_run (RunLoadError)
    so every caller - compare here, the webapp run list - is covered by one guard."""
    resolved = _resolve_run_id(runs_dir, run_id)
    if resolved != run_id:
        print(f"resolved '{run_id}' -> {resolved}")
    path = runs_dir / f"{resolved}.json"
    if not path.is_file():
        raise RunRefused(
            f"run '{run_id}' not found at {path} (pass a run label or file stem from {runs_dir})")
    try:
        return load_run(path)
    except RunLoadError as exc:
        raise RunRefused(str(exc)) from exc


def cmd_serve(args, *, lab_dir: Path | None = None) -> int:
    """Serve the read-only diff webapp over the lab's runs dir until Ctrl-C."""
    lab_dir = _lab_dir_from(args, lab_dir)
    webapp.serve(lab_dir / "runs", port=args.port)
    return 0


def cmd_compare(args, *, lab_dir: Path | None = None) -> int:
    """Diff RUN_B (new) against RUN_A (baseline): one line per non-same row
    ("KIND  utterance  (source)") plus a "N changed of M rows" summary. The
    same-locale guarantee lives in the webapp per spec; the CLI only warns
    (loudly) on a locale mismatch. --expect-zero-changes turns any non-same
    row into exit 1 (the A/A gate: comparing a run against itself must be
    all-same)."""
    lab_dir = _lab_dir_from(args, lab_dir)
    try:
        run_old = _load_run_or_refuse(lab_dir / "runs", args.run_a)
        run_new = _load_run_or_refuse(lab_dir / "runs", args.run_b)
    except RunRefused as exc:
        print(f"refusing to compare: {exc}", file=sys.stderr)
        return 1
    if run_old.skill_id != run_new.skill_id:
        msg_skill = f"different lab skills: {run_old.skill_id} vs {run_new.skill_id}"
        if args.expect_zero_changes:
            print(f"refusing to compare: {msg_skill} (an A/A gate requires the same skill)", file=sys.stderr)
            return 1
        print(f"*** WARNING *** {msg_skill}", file=sys.stderr)
    if (run_old.build_etag is not None and run_new.build_etag is not None
            and run_old.build_etag != run_new.build_etag):
        msg_etag = (f"different builds (eTag {run_old.build_etag} vs {run_new.build_etag}): "
                    "the two runs probed different model builds")
        if args.expect_zero_changes:
            print(f"refusing to compare: {msg_etag} (an A/A gate requires the same build)", file=sys.stderr)
            return 1
        print(f"*** WARNING *** {msg_etag}", file=sys.stderr)
    if run_old.model_sha256 != run_new.model_sha256:
        msg = (f"model sha mismatch: {args.run_a} probed {run_old.model_sha256[:12]}, "
               f"{args.run_b} probed {run_new.model_sha256[:12]}")
        if args.expect_zero_changes:
            # The A/A claim would be fiction: the two runs probed DIFFERENT
            # models (a --skip-deploy run against the wrong file records the
            # file's sha, not what is deployed).
            print(f"refusing to compare: {msg} (an A/A gate requires the same model)", file=sys.stderr)
            return 1
        print(f"*** WARNING *** {msg}", file=sys.stderr)
    if run_old.locale != run_new.locale:
        print(
            f"*** WARNING: run {run_old.id} is locale {run_old.locale} but run "
            f"{run_new.id} is locale {run_new.locale}: a cross-locale diff judges "
            "rows against incomparable models ***", file=sys.stderr)
    diffs = run_new.diff_against(run_old)
    changed = 0
    for d in diffs:
        if d.kind == "same":
            continue
        changed += 1
        print(f"{d.kind}  {d.utterance}  ({d.source})")
    print(f"{changed} changed of {len(diffs)} rows")
    if args.expect_zero_changes and changed:
        return 1
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="nlu-lab",
        description="Deploy-and-probe harness for interaction models against a dedicated lab skill")
    sub = parser.add_subparsers(dest="command", required=True)
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--lab-dir", type=Path, default=None, metavar="DIR",
                        help=f"lab directory holding lab.json and runs/ "
                             f"(default: {LAB_DIR_DEFAULT})")
    init = sub.add_parser(
        "init-skill", parents=[common],
        help="create (or pin) the dedicated lab skill and write lab.json")
    init.add_argument("--name", default=DEFAULT_SKILL_NAME,
                      help=f"skill name stamped into every publishing locale "
                           f"(default: {DEFAULT_SKILL_NAME!r})")
    init.add_argument("--invocation", default=DEFAULT_INVOCATION,
                      help=f"invocation name substituted into every deployed model "
                           f"(default: {DEFAULT_INVOCATION!r})")
    init.add_argument("--endpoint", default=None, metavar="URI",
                      help="HTTPS endpoint Alexa posts skill requests to "
                           "(required when creating; a console-created skill already has one)")
    init.add_argument("--pin-id", default=None, metavar="SKILL_ID",
                      help="record an existing (console-created) skill instead of "
                           "creating one; its live name must match --name")
    init.add_argument("--force", action="store_true",
                      help="overwrite an existing lab.json")
    init.set_defaults(func=cmd_init)
    run = sub.add_parser("run", parents=[common],
                         help="deploy a model to the lab skill and run the battery")
    run.add_argument("--locale", required=True)
    source = run.add_mutually_exclusive_group(required=True)
    source.add_argument("--model", type=Path, default=None,
                        help="path to a model JSON file (interactionModel wrapper or bare languageModel)")
    source.add_argument("--template", action="store_true",
                        help="build the locale's model in memory from its YAML template")
    run.add_argument("--label", required=True, help="run label; slug becomes part of the run id")
    run.add_argument("--workingset", type=Path, default=None,
                     help="optional workingset YAML in the fixture format (its locale must match)")
    run.add_argument("--skip-deploy", action="store_true",
                     help="probe the ALREADY-deployed model (A/A noise control: deploy once, "
                          "probe twice; an identical redeploy mints no new eTag and would "
                          "time out the build wait, live-verified 2026-11-21)")
    run.add_argument("-k", dest="filter_k", default=None, metavar="SUBSTR",
                     help="substring filter on utterances, applied across both battery sources")
    run.set_defaults(func=cmd_run)
    export = sub.add_parser(
        "export",
        help="write a deploy-ready model payload stamped with an explicit invocation name")
    export.add_argument("--locale", required=True)
    source = export.add_mutually_exclusive_group(required=True)
    source.add_argument("--model", type=Path, default=None,
                        help="path to a model JSON file (interactionModel wrapper or bare languageModel)")
    source.add_argument("--template", action="store_true",
                        help="build the locale's model in memory from its YAML template")
    export.add_argument("--label", required=True,
                        help="export label; the slug becomes part of the file name")
    export.add_argument("--invocation-name", required=True, metavar="NAME",
                        help="invocation name stamped into the payload; MANDATORY because the "
                             "plugin's custom-model import deploys it verbatim (the lab never "
                             "substitutes the source model's or the lab's name)")
    export.add_argument("--out", type=Path, default=None, metavar="DIR",
                        help="output directory (default: the current directory)")
    export.set_defaults(func=cmd_export)
    compare = sub.add_parser(
        "compare", parents=[common], help="diff two runs row by row (RUN_B against RUN_A)")
    compare.add_argument("run_a", metavar="RUN_A",
                         help="baseline run id (a run file stem under runs/)")
    compare.add_argument("run_b", metavar="RUN_B",
                         help="new run id; its rows are diffed against RUN_A's")
    compare.add_argument("--expect-zero-changes", action="store_true",
                         help="exit 1 when any row is not 'same' (the A/A gate)")
    compare.set_defaults(func=cmd_compare)
    serve = sub.add_parser(
        "serve", parents=[common],
        help="serve the read-only diff webapp (run list, matrix, legend) for LAN browsing")
    serve.add_argument("--port", type=int, default=webapp.DEFAULT_PORT, metavar="N",
                       help=f"TCP port to listen on (default: {webapp.DEFAULT_PORT})")
    serve.set_defaults(func=cmd_serve)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
