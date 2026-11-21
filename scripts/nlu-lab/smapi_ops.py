"""SMAPI operations for the lab: live skill-name read, model deploy, build
wait, and the profile-nlu battery runner.

Process running goes through nlu_wrapper_battery._run_ask (the repo's ask-CLI
runner: full argv including the leading "ask", CompletedProcess returned, the
CALLER checks the exit code). Response parsing reuses SmapiClient where the
repo already owns it: parse_profile_nlu_result is a staticmethod, so it is
called on the class (no client instance is constructed; its __init__ demands
a live skill/locale pair this module does not have). nlu_wrapper_battery's
own profile_nlu returns only the intent name and misses slots/considered
intents, which the run schema needs.
"""
import json
import os
import subprocess
import tempfile
import time
from pathlib import Path

from nlu_wrapper_battery import _run_ask
from runs import RowStatus
from smapi_client import SmapiClient, SmapiError

BUILD_SUCCEEDED = "SUCCEEDED"
BUILD_FAILED = "FAILED"
BUILD_IN_PROGRESS = "IN_PROGRESS"

POLL_INTERVAL_S = 10.0

_MISSING = object()


def default_delay() -> float:
    """SMAPI_DELAY with the repo default (1.5s), same source as
    nlu_wrapper_battery.main's inter-call pacing."""
    return float(os.environ.get("SMAPI_DELAY", "1.5"))


def _ask_json(args: list[str]) -> dict:
    """Run one `ask smapi ...` subcommand and return its parsed JSON stdout.

    Raises SmapiError on nonzero exit, unparseable output, or a non-object
    payload, so run_battery_rows can translate failures into ERROR rows.
    """
    result = _run_ask(["ask", "smapi", *args])
    label = " ".join(args[:2])
    if result.returncode != 0:
        detail = (result.stderr or result.stdout or "").strip()[:300]
        raise SmapiError(f"ask {label} exited {result.returncode}: {detail}")
    try:
        data = json.loads(result.stdout)
    except json.JSONDecodeError as exc:
        raise SmapiError(f"ask {label} returned unparseable output: {exc}") from exc
    if not isinstance(data, dict):
        raise SmapiError(f"ask {label} returned {type(data).__name__}, expected a JSON object")
    return data


def parse_skill_name(payload: dict) -> str | None:
    """en-US publishing name from a get-skill-manifest payload (first locale
    as fallback), None when no publishable name exists. The outer body
    wrapper is tolerated (the CLI's --full-response mode adds it).

    The installed ask CLI has no get-skill subcommand; get-skill-manifest is
    the live-name source lab_config's deploy guard checks against.
    """
    data = payload.get("body", payload) if isinstance(payload, dict) else {}
    manifest = data.get("manifest") or {}
    locales = (manifest.get("publishingInformation") or {}).get("locales") or {}
    entry = locales.get("en-US")
    if entry is None:
        for candidate in locales.values():
            entry = candidate
            break
    if not isinstance(entry, dict):
        return None
    return entry.get("name") or None


def get_skill_name(skill_id: str) -> str:
    payload = _ask_json(["smapi", "get-skill-manifest", "--skill-id", skill_id,
                         "--stage", "development"])
    name = parse_skill_name(payload)
    if not name:
        raise SmapiError(f"skill {skill_id} manifest carries no publishing name")
    return name


def deploy_model(skill_id: str, locale: str, payload: dict) -> bool:
    """Deploy one locale's interaction model; the payload is written to a tmp
    file (the CLI's file: form) and unlinked after the call. Raises
    SmapiError on nonzero exit."""
    tmp = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8")
    try:
        json.dump(payload, tmp, ensure_ascii=False)
        tmp.close()
        result = _run_ask(["ask", "smapi", "set-interaction-model",
                           "--skill-id", skill_id, "--stage", "development",
                           "--locale", locale,
                           "--interaction-model", f"file:{tmp.name}"])
        if result.returncode != 0:
            detail = (result.stderr or result.stdout or "").strip()[:300]
            raise SmapiError(
                f"ask smapi set-interaction-model ({locale}) exited "
                f"{result.returncode}: {detail}")
        return True
    finally:
        Path(tmp.name).unlink(missing_ok=True)


def parse_build_status(payload: dict) -> str:
    """Map a get-skill-status payload to SUCCEEDED / FAILED / IN_PROGRESS.

    Pins the SMAPI v1 per-resource shape the repo's own
    SmapiManagement.WaitForSkillStatusAsync consumes
    (interactionModel.lastModified.{status,locale}); the outer body wrapper is
    optional. An absent lastModified, an absent locale, or any status other
    than SUCCEEDED/FAILED reads IN_PROGRESS (keep polling): a build whose
    resource row has not appeared yet cannot be judged.
    """
    data = payload.get("body", payload) if isinstance(payload, dict) else {}
    last_modified = (data.get("interactionModel") or {}).get("lastModified") or {}
    if not last_modified.get("locale"):
        return BUILD_IN_PROGRESS
    status = last_modified.get("status")
    if status in (BUILD_SUCCEEDED, BUILD_FAILED):
        return status
    return BUILD_IN_PROGRESS


def wait_for_build(skill_id: str, locale: str, timeout_s: float = 600) -> bool:
    """Poll get-skill-status (no --stage flag on this subcommand) every
    POLL_INTERVAL_S until SUCCEEDED (True), FAILED (raise), or the deadline
    (False). A poll that itself fails (CLI error, bad JSON, or the runner's
    subprocess.TimeoutExpired) reads IN_PROGRESS: the deadline, not a
    transient CLI hiccup, ends the wait."""
    deadline = time.monotonic() + timeout_s
    while True:
        try:
            status = parse_build_status(
                _ask_json(["smapi", "get-skill-status", "--skill-id", skill_id]))
        except (SmapiError, subprocess.TimeoutExpired):
            status = BUILD_IN_PROGRESS
        if status == BUILD_SUCCEEDED:
            return True
        if status == BUILD_FAILED:
            raise SmapiError(f"skill {skill_id} build FAILED for {locale}")
        if time.monotonic() >= deadline:
            return False
        time.sleep(POLL_INTERVAL_S)


def profile_row(skill_id: str, locale: str, utterance: str) -> dict:
    """One raw profile-nlu response dict; the run command stores this in
    RowResult.raw."""
    return _ask_json(["smapi", "profile-nlu", "--skill-id", skill_id,
                      "--stage", "development", "--locale", locale,
                      "--utterance", utterance])


def _considered_names(response: dict) -> list[str]:
    return [c.get("name") for c in response.get("consideredIntents", [])
            if isinstance(c, dict)]


def run_battery_rows(profile_fn, rows, delay: float | None = None) -> list[dict]:
    """Run the battery rows through profile_fn with SMAPI pacing and the
    JF-406 single-transient retry.

    profile_fn takes the utterance and returns the raw profile-nlu response
    dict (profile_row partial-applied). rows are battery.BatteryRow objects
    (anything with .utterance works); the result preserves input order, one
    dict per row: {utterance, selected_intent, slots, considered_intents,
    status} with status from runs.RowStatus. Every pair of consecutive
    profile calls (retries included) is spaced by `delay` (None =
    SMAPI_DELAY env, repo default 1.5). A row whose profile call raises or
    returns a non-dict is retried exactly once (the JF-406 single-500
    class); a second failure
    maps the row to ERROR and the loop continues, so one bad row never
    aborts the run. A response without selectedIntent maps to NO_SELECTION:
    SmapiClient.parse_profile_nlu_result raises SmapiError on that shape and
    the thin catch converts it, keeping slots/considered parsing in the one
    repo-owned place.
    """
    if delay is None:
        delay = default_delay()
    out: list[dict] = []
    first_call = True
    for row in rows:
        utterance = row.utterance
        response = _MISSING
        for _attempt in (1, 2):
            if first_call:
                first_call = False
            else:
                time.sleep(delay)
            try:
                candidate = profile_fn(utterance)
            except Exception:  # noqa: BLE001 - one JF-406 retry, then ERROR row
                continue
            # Assign only a dict, never the bad value: the sentinel must stay
            # _MISSING for every failed attempt shape (raise, None, non-dict)
            # or a non-dict would flow into the parser and abort the battery.
            if isinstance(candidate, dict):
                response = candidate
                break
        if response is _MISSING:
            out.append({"utterance": utterance, "selected_intent": None,
                        "slots": {}, "considered_intents": [],
                        "status": RowStatus.ERROR})
            continue
        try:
            parsed = SmapiClient.parse_profile_nlu_result(response)
        except SmapiError:
            out.append({"utterance": utterance, "selected_intent": None,
                        "slots": {}, "considered_intents": _considered_names(response),
                        "status": RowStatus.NO_SELECTION})
            continue
        out.append({"utterance": utterance, "selected_intent": parsed["intent"],
                    "slots": parsed["slots"],
                    "considered_intents": _considered_names(response),
                    "status": RowStatus.OK})
    return out
