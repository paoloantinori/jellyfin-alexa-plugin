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
import contextlib
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

    args carries the full argv AFTER the leading "ask" (callers pass the
    "smapi" prefix themselves); prepending it here too would emit
    `ask smapi smapi ...`, which the CLI rejects with "Command not recognized"
    (caught by init-skill's argv-pinning tests, 2026-11-21).

    Raises SmapiError on nonzero exit, timeout (the runner's
    subprocess.TimeoutExpired converted here, so every caller of this choke
    point refuses cleanly instead of tracing), unparseable output, or a
    non-object payload, so run_battery_rows can translate failures into
    ERROR rows.
    """
    label = " ".join(args[:2])
    try:
        result = _run_ask(["ask", *args])
    except subprocess.TimeoutExpired as exc:
        raise SmapiError(f"ask {label} timed out after {exc.timeout}s") from exc
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


def _unwrap_body(payload: dict) -> dict:
    """The payload's own body: the outer "body" wrapper when the CLI's
    --full-response mode added it, the payload itself otherwise; a non-dict
    reads as {} (every caller then finds no fields)."""
    if not isinstance(payload, dict):
        return {}
    return payload.get("body", payload)


def parse_skill_name(payload: dict) -> str | None:
    """en-US publishing name from a get-skill-manifest payload (first locale
    as fallback), None when no publishable name exists. The outer body
    wrapper is tolerated (the CLI's --full-response mode adds it).

    The installed ask CLI has no get-skill subcommand; get-skill-manifest is
    the live-name source lab_config's deploy guard checks against.
    """
    data = _unwrap_body(payload)
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


def parse_skill_id(payload: dict) -> str | None:
    """skillId from a create-skill-for-vendor payload (the SMAPI v1 createSkill
    response; Alexa.NET's Skills.Create, which SmapiManagement.CreateSkillAsync
    calls, parses the same field). The outer body wrapper is tolerated like
    every other parser here. None when no usable skillId is present."""
    data = _unwrap_body(payload)
    skill_id = data.get("skillId")
    return skill_id if isinstance(skill_id, str) and skill_id else None


@contextlib.contextmanager
def _payload_file(payload: dict):
    """Write payload to a tmp JSON file and yield its path for the CLI's
    `file:{path}` form; always unlinked afterwards. The one tmp-file dance
    behind every file-passing smapi call (create-skill, set-interaction-model)."""
    tmp = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8")
    try:
        json.dump(payload, tmp, ensure_ascii=False)
        tmp.close()
        yield tmp.name
    finally:
        Path(tmp.name).unlink(missing_ok=True)


def create_skill(manifest_payload: dict) -> str:
    """Create a skill via `ask smapi create-skill-for-vendor --manifest file:`
    and return its skillId. The installed CLI takes no --vendor-id flag (only
    --manifest/-p/--full-response/--debug; verified against its --help): the
    vendor comes from the ask profile's own state. Raises SmapiError on nonzero
    exit, unparseable output, or a payload without skillId."""
    with _payload_file(manifest_payload) as path:
        data = _ask_json(["smapi", "create-skill-for-vendor",
                          "--manifest", f"file:{path}"])
    skill_id = parse_skill_id(data)
    if not skill_id:
        raise SmapiError("create-skill response carries no skillId")
    return skill_id


def has_create_skill_command() -> bool:
    """True when the installed ask CLI knows `smapi create-skill-for-vendor`.
    Resolved locally via --help (the CLI answers before any network call): an
    unknown subcommand exits 1 with 'Command not recognized' on stderr, a
    known one exits 0 (probed on this machine)."""
    try:
        result = _run_ask(["ask", "smapi", "create-skill-for-vendor", "--help"])
    except subprocess.TimeoutExpired as exc:
        raise SmapiError(
            "timed out probing the ask CLI for create-skill-for-vendor") from exc
    return result.returncode == 0


def deploy_model(skill_id: str, locale: str, payload: dict) -> bool:
    """Deploy one locale's interaction model via the CLI's file: form (the
    shared _payload_file tmp dance). Raises SmapiError on nonzero exit."""
    with _payload_file(payload) as path:
        result = _run_ask(["ask", "smapi", "set-interaction-model",
                           "--skill-id", skill_id, "--stage", "development",
                           "--locale", locale,
                           "--interaction-model", f"file:{path}"])
    if result.returncode != 0:
        detail = (result.stderr or result.stdout or "").strip()[:300]
        raise SmapiError(
            f"ask smapi set-interaction-model ({locale}) exited "
            f"{result.returncode}: {detail}")
    return True


def _locale_entry(payload: dict, locale: str) -> dict | None:
    """The per-locale interactionModel entry, LIVE shape (probed read-only
    2026-11-21 against the dev skill): interactionModel[<locale>] = {eTag,
    lastUpdateRequest: {status, buildDetails}}; the outer body wrapper is
    optional. None when the locale is absent or any level is malformed
    (non-dict): guarded, never raises. There is NO lastModified key in the
    live payload (the plan's earlier pinned shape was fiction; JF-938 tracks
    the C# consumer of the same drift)."""
    data = _unwrap_body(payload)
    im = data.get("interactionModel")
    if not isinstance(im, dict):
        return None
    entry = im.get(locale)
    return entry if isinstance(entry, dict) and entry else None


def _entry_status(entry: dict | None) -> str:
    """Terminal only on entry.lastUpdateRequest.status. The locale match is
    structural (the entry is fetched BY locale), so a foreign locale's row
    cannot end this locale's wait. Absent entry reads IN_PROGRESS: a build
    whose entry has not appeared yet cannot be judged."""
    if entry is None:
        return BUILD_IN_PROGRESS
    req = entry.get("lastUpdateRequest")
    if not isinstance(req, dict):
        return BUILD_IN_PROGRESS
    status = req.get("status")
    if status in (BUILD_SUCCEEDED, BUILD_FAILED):
        return status
    return BUILD_IN_PROGRESS


def _status_argv(skill_id: str) -> list[str]:
    """The ONE get-skill-status argv (no --stage flag on this subcommand):
    the baseline read and the poll must never drift apart."""
    return ["smapi", "get-skill-status", "--skill-id", skill_id]


def get_build_entry(skill_id: str, locale: str) -> dict | None:
    """The current per-locale entry (the wait baseline WITNESS): the eTag
    changes per build, so whole-entry equality is the transition token.
    RAISES SmapiError on read failure: the CLI distinguishes an absent entry
    (fresh skill, silent baseline=None) from an unreadable one (degraded
    guard, loud warning)."""
    return _locale_entry(_ask_json(_status_argv(skill_id)), locale)


def wait_for_build(skill_id: str, locale: str, timeout_s: float = 600,
                   baseline: dict | None = None) -> bool:
    """Poll get-skill-status every POLL_INTERVAL_S until SUCCEEDED (True),
    FAILED (raise), or the deadline (False). JF-934: an entry identical to
    the PRE-DEPLOY baseline (eTag unchanged) is the previous build's state
    and is never trusted (stale SUCCEEDED would profile the old model
    against the new run's sha256); only an entry that differs from the
    baseline (or any entry, when baseline is None: the first build on a
    fresh skill) may read terminal. A poll that itself fails reads
    IN_PROGRESS: _ask_json converts every failure shape (nonzero exit, bad
    JSON, the runner's subprocess.TimeoutExpired) into SmapiError, and the
    deadline, not a transient CLI hiccup, ends the wait."""
    deadline = time.monotonic() + timeout_s
    while True:
        try:
            entry = _locale_entry(_ask_json(_status_argv(skill_id)), locale)
            # Stale predicate on the eTag ALONE (the per-build token): the old
            # build's own IN_PROGRESS->SUCCEEDED flip keeps the same eTag and
            # must not satisfy the transition when the baseline was captured
            # mid-build (re-review residual (a)).
            stale = (baseline is not None and entry is not None
                     and entry.get("eTag") is not None
                     and entry.get("eTag") == baseline.get("eTag"))
            status = BUILD_IN_PROGRESS if stale else _entry_status(entry)
        except SmapiError:
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


def considered_hypotheses(response: dict) -> list[dict]:
    """The FULL consideredIntents hypotheses (JF-968): one dict per entry,
    order preserved (the SAME intent can appear multiple times with
    different slot segmentations; entry rank is as received, unverified).

    Live shape (probed 2026-11-25): each entry is a full Intent object
    {name, slots: {slot_name: {value, resolutions?}}}; there is NO score
    field. Per entry: {"intent": name, "slots": {slot: value}, "er":
    {slot: [{authority, code, canonical?}]}} where canonical is present
    only on ER_SUCCESS_MATCH (values[0].value.name). Guarded, never raises:
    a malformed entry or slot degrades to empty fields instead of aborting
    the battery. _considered_names and the suite parser's "considered" both
    derive from this ONE function."""
    out: list[dict] = []
    for entry in response.get("consideredIntents") or []:
        if not isinstance(entry, dict):
            continue
        slots: dict = {}
        er: dict = {}
        raw_slots = entry.get("slots")
        if isinstance(raw_slots, dict):
            for slot_name, slot in raw_slots.items():
                if not isinstance(slot, dict):
                    slots[str(slot_name)] = slot
                    continue
                slots[str(slot_name)] = slot.get("value")
                per_authority = []
                resolutions = slot.get("resolutions")
                if isinstance(resolutions, dict):
                    authorities = resolutions.get("resolutionsPerAuthority") or []
                    if isinstance(authorities, list):
                        for auth in authorities:
                            if not isinstance(auth, dict):
                                continue
                            status = auth.get("status")
                            code = status.get("code") if isinstance(status, dict) else None
                            rec: dict = {"authority": auth.get("authority"),
                                         "code": code}
                            if code == "ER_SUCCESS_MATCH":
                                values = auth.get("values")
                                if (isinstance(values, list) and values
                                        and isinstance(values[0], dict)):
                                    v = values[0].get("value")
                                    if isinstance(v, dict) and v.get("name") is not None:
                                        rec["canonical"] = v.get("name")
                            per_authority.append(rec)
                if per_authority:
                    er[str(slot_name)] = per_authority
        out.append({"intent": entry.get("name"), "slots": slots, "er": er})
    return out


def _considered_names(response: dict) -> list[str]:
    """Names-only projection of considered_hypotheses (the ONE extractor)."""
    return [h["intent"] for h in considered_hypotheses(response)]


def run_battery_rows(profile_fn, rows, delay: float | None = None) -> list[dict]:
    """Run the battery rows through profile_fn with SMAPI pacing and the
    JF-406 single-transient retry.

    profile_fn takes the utterance and returns the raw profile-nlu response
    dict (profile_row partial-applied). rows are battery.BatteryRow objects
    (anything with .utterance works); the result preserves input order, one
    dict per row: {utterance, selected_intent, slots, considered_intents,
    considered_hypotheses, status} with status from runs.RowStatus. Every pair of consecutive
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
                        "considered_hypotheses": [],
                        "status": RowStatus.ERROR})
            continue
        try:
            parsed = SmapiClient.parse_profile_nlu_result(response)
        except SmapiError:
            status = RowStatus.NO_SELECTION
            selected = None
            slots: dict = {}
        else:
            status = RowStatus.OK
            selected = parsed["intent"]
            slots = parsed["slots"]
        # ONE extraction per row: the names-only column is a projection of
        # the hypotheses list (_considered_names keeps the same function).
        hyps = considered_hypotheses(response)
        out.append({"utterance": utterance, "selected_intent": selected,
                    "slots": slots,
                    "considered_intents": [h["intent"] for h in hyps],
                    "considered_hypotheses": hyps,
                    "status": status})
    return out
