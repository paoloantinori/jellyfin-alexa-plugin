"""E2E integration tests exercising the full Alexa -> skill -> Jellyfin chain.

Uses SMAPI simulate-skill to send utterances through Alexa's full pipeline,
then validates the skill's response and optional Jellyfin side effects.

Requires:
- A running Jellyfin server accessible to the Alexa skill endpoint
- --jellyfin-url, --jellyfin-api-key, --jellyfin-user CLI options (or env vars)
"""

from __future__ import annotations

import logging
import time
import xml.etree.ElementTree as ET

import pytest

from smapi_client import SmapiClient, SmapiError

logger = logging.getLogger("e2e.test")

# Fixture keys that assert on the skill's response body (JF-510). A fixture
# carrying any of them needs the invocation body simulate-skill returns.
RESPONSE_MARKER_KEYS = (
    "expected_speech_contains", "expected_reprompt",
    "expected_end_session", "expected_stream_url_contains",
    "expected_play_or_gate_tell",
)


@pytest.fixture
def e2e_fixture(request):
    """Indirect fixture: each parametrized case is a dict from e2e_*.yaml."""
    return request.param


@pytest.fixture
def e2e_smapi_client(skill_id, smapi_delay, e2e_fixture):
    """Per-locale SmapiClient for E2E simulation."""
    return SmapiClient(
        skill_id=skill_id,
        locale=e2e_fixture["locale"],
        delay=smapi_delay,
        invocation_name=e2e_fixture.get("invocation_name", ""),
    )


# ---------------------------------------------------------------------------
# Cross-test simulate-skill session tracking (locale-scoped, conditional)
# ---------------------------------------------------------------------------

# Locales whose most recent simulation in this process left the skill
# session open (shouldEndSession != true). simulate-skill session state is
# per device locale, so the tracking is keyed per locale.
_open_sessions: set[str] = set()

# Locales already given one unconditional reset in this process: the first
# reset of a run guards against a stale open session left by an earlier
# crashed run, where no in-process tracking exists.
_reset_initialized: set[str] = set()


def _request_locale(request: pytest.FixtureRequest) -> str:
    """Resolve the locale under test from a test's parametrized fixtures.

    The fixture-dict params (e2e/smoke/reliability) carry their locale
    inside the dict; test_e2e_fast_mode parametrizes ``locale`` directly.
    """
    callspec = getattr(request.node, "callspec", None)
    params = callspec.params if callspec is not None else {}
    direct = params.get("locale")
    if isinstance(direct, str) and direct:
        return direct
    for value in params.values():
        if isinstance(value, dict) and isinstance(value.get("locale"), str):
            return value["locale"]
    return "it-IT"


def _bare_stop(skill_id: str, locale: str, delay: float) -> None:
    """Best-effort bare-'stop' close of a persistent simulate-skill session."""
    client = SmapiClient(
        skill_id=skill_id, locale=locale, delay=delay, invocation_name=""
    )
    try:
        client.simulate("stop")
    except Exception as exc:  # noqa: BLE001 - reset is best effort
        logger.warning("Session reset simulation failed (%s, continuing): %s", locale, exc)


def _record_session_state(locale: str, response: dict) -> None:
    """Track whether the skill session was left open by *response*.

    Drives the conditional reset: play responses and Tells end the session
    themselves, so the next test pays no reset; only an actually-open
    session (reprompt/disambiguation) does.
    """
    body = _extract_response_body(_extract_skill_response(response))
    if body and body.get("shouldEndSession") is not True:
        _open_sessions.add(locale)
    else:
        _open_sessions.discard(locale)


def _ensure_session_closed(skill_id: str, locale: str, delay: float) -> None:
    """The ONE reset policy: close an open session in *locale* if needed.

    First use of a locale in the process always resets once (guards against
    a stale open session left by an earlier crashed run, where no
    in-process tracking exists); afterwards only when the previous
    simulation in that locale left the session open (_record_session_state).
    """
    if locale in _reset_initialized and locale not in _open_sessions:
        return
    _reset_initialized.add(locale)
    _open_sessions.discard(locale)
    _bare_stop(skill_id, locale, delay)


@pytest.fixture(autouse=True)
def _reset_simulation_session(dry_run, skill_id, smapi_delay, request):
    """End a persistent open simulate-skill session before each E2E test.

    simulate-skill (development stage) PERSISTS the session across sequential
    simulations: a FindSong elicitation left open by one fixture rides along as
    FindSongSessionData, and the controller routes every subsequent IntentRequest to
    FindSongIntent regardless of the utterance (observed live 2026-08-28: 'pausa' and
    'metti una canzone dei soul coughin' both resolved to FindSongIntent; verified by
    inspecting the inherited session attributes in the simulation payload). A BARE
    'stop' (no invocation prefix: in the open dialog it is captured into the elicited
    slot, where the handler's cancel-word escape hatch ends the session with a Tell)
    resets the state. Prefixed one-shots do NOT work here: the dialog capture includes
    the invocation prefix in the slot value. Best-effort: a failed reset surfaces
    downstream as the recognizable find-song-hijack pattern.

    Locale-scoped and conditional since JF-511 (was hardcoded it-IT, which
    made every non-it-IT fixture run with a reset that fired in the wrong
    locale and failed benignly): the reset runs in the test's own locale and
    only when needed. The first test in a locale always resets once (guards
    against a stale open session from an earlier run); afterwards only when
    the previous simulation in that locale left the session open (tracked by
    _record_session_state). Smoke tests opt out entirely: their two-step
    shape opens the skill fresh per test and manages its own cleanup, so the
    open itself serves as the reset.

    No-op in dry-run mode: this autouse fixture fires BEFORE each test's own
    dry-run skip, so an unguarded simulate() here issued one real
    ``ask smapi simulate-skill`` subprocess per collected test (JF-435).
    """
    if dry_run:
        return
    if "smoke_fixture" in request.fixturenames:
        return

    locale = _request_locale(request)
    _ensure_session_closed(skill_id, locale, smapi_delay)


@pytest.mark.e2e
def test_e2e_full_chain(dry_run, e2e_fixture, e2e_smapi_client, jellyfin_client):
    """Full-chain test: utterance -> NLU -> skill -> response + side effects."""
    utterance = e2e_fixture["utterance"]
    expected_intent = e2e_fixture["expected_intent"]
    expected_slots = e2e_fixture.get("expected_slots", {})
    expected_response_type = e2e_fixture.get("expected_response_type", "any")
    skip_reason = e2e_fixture.get("skip_reason", "")
    locale = e2e_fixture["locale"]

    if dry_run:
        assert utterance, f"Empty utterance in {e2e_fixture.get('source', '?')}"
        assert expected_intent, f"Missing expected_intent for '{utterance}'"
        assert expected_response_type in ("any", "speech", "directive"), (
            f"Invalid expected_response_type: {expected_response_type}"
        )
        if expected_response_type == "directive":
            assert e2e_fixture.get("expected_directive_type"), (
                f"expected_directive_type required when response_type is 'directive'"
            )
        assert e2e_fixture.get("expected_reprompt") in (None, True), (
            f"expected_reprompt must be true or omitted for '{utterance}'"
        )
        if "expected_end_session" in e2e_fixture:
            assert isinstance(e2e_fixture["expected_end_session"], bool), (
                f"expected_end_session must be a bool for '{utterance}'"
            )
        for str_key in ("expected_speech_contains", "expected_stream_url_contains"):
            val = e2e_fixture.get(str_key, "")
            assert not val or isinstance(val, str), (
                f"{str_key} must be a non-empty string or omitted for '{utterance}'"
            )
        if skip_reason:
            assert isinstance(skip_reason, str) and skip_reason.strip(), (
                f"skip_reason must be a non-empty string for '{utterance}'"
            )
        pytest.skip("dry-run mode: E2E simulation skipped")

    # A tracked, documented non-passing state (known model regression, platform
    # limitation): keep the fixture visible, record why it is not asserted today.
    # Skipping is only legitimate with a reason that names the evidence.
    if skip_reason:
        pytest.skip(f"'{utterance}' ({locale}): {skip_reason}")

    logger.info(
        "E2E [%s] (%s) expecting %s (response: %s)",
        utterance, locale, expected_intent, expected_response_type,
    )

    # --- Run full simulation via SMAPI ---
    # Bounded retry when the fixture asserts on the response body but the
    # simulation resolved the intent WITHOUT invoking the skill (empty
    # skillExecutionInfo): an intermittent simulation-side throttle artifact
    # under sustained SMAPI traffic (live 2026-09-07: consecutive no-invocation
    # simulations for 'voglio guardare il film ada' inside a ~35s window that
    # the next test's retry escaped; ~1-2 tests per full run). 3 attempts with
    # a settle pause cover the observed window; a genuinely broken endpoint
    # fails all attempts, so this smooths infra flake only.
    needs_body = (
        expected_response_type != "any"
        or any(k in e2e_fixture for k in RESPONSE_MARKER_KEYS)
    )
    try:
        response = _simulate_with_invocation_retry(
            e2e_smapi_client, utterance, expected_intent, needs_body
        )
    except SmapiError as exc:
        # Session state after a failed simulation is unknown; assume open so
        # the next test resets (preserves the unconditional-reset safety net
        # that existed before the reset became conditional, JF-511).
        _open_sessions.add(locale)
        pytest.fail(f"SMAPI simulation error for '{utterance}' ({locale}): {exc}")

    _record_session_state(locale, response)

    # --- Parse NLU result ---
    result = SmapiClient.parse_nlu_result(response)
    resolved_intent = result["intent"]

    assert resolved_intent == expected_intent, (
        f"Intent mismatch for '{utterance}' ({locale}):\n"
        f"  expected: {expected_intent}\n"
        f"  actual:   {resolved_intent}"
    )

    # --- Slot assertions ---
    _assert_slots(utterance, locale, result["slots"], expected_slots)

    # --- Response assertions ---
    skill_response = _extract_skill_response(response)
    _assert_response_type(
        utterance, locale, skill_response, expected_response_type, e2e_fixture
    )
    _assert_response_markers(utterance, locale, skill_response, e2e_fixture)

    # --- SSML validity: any SSML output speech must be well-formed XML
    # (catches reserved-char escaping regressions like JF-323) ---
    _assert_ssml_valid(skill_response, utterance, locale)

    # --- APL directive validation (when expected) ---
    expected_apl = e2e_fixture.get("expected_apl")
    if expected_apl:
        apl_directives = _extract_apl_directives(skill_response)
        assert apl_directives, (
            f"Expected APL directives for '{utterance}' ({locale}), "
            f"but none found in response"
        )
        for d in apl_directives:
            apl_errors = _validate_apl_directive(d)
            assert not apl_errors, (
                f"APL validation errors for '{utterance}' ({locale}):\n"
                + "\n".join(f"  - {e}" for e in apl_errors)
            )
        logger.info(
            "  APL OK: %d directive(s) validated for [%s]",
            len(apl_directives), utterance[:30],
        )

    # --- Jellyfin side-effect checks ---
    if jellyfin_client is not None:
        _check_side_effects(
            expected_intent, jellyfin_client
        )

    logger.info("  E2E PASS: [%s] -> %s", utterance[:30], resolved_intent)


def _extract_skill_response(simulation: dict) -> dict:
    """Extract the skill's response info from the simulation result.

    ``skillExecutionInfo`` is a SIBLING of ``alexaExecutionInfo`` under
    ``result`` in simulate-skill output (verified against raw SMAPI JSON,
    JF-510). The old code read ``result.alexaExecutionInfo.skillExecutionInfo``
    (always empty), which is why every historical fixture could only use
    ``expected_response_type: any``: the speech/directive assertions never
    saw the response body at all.
    """
    result = simulation.get("result", {})
    skill_info = result.get("skillExecutionInfo")
    if isinstance(skill_info, dict):
        return skill_info
    # Legacy/wrong path kept only as a harmless fallback.
    return result.get("alexaExecutionInfo", {}).get("skillExecutionInfo", {})


def _extract_response_body(skill_response: dict) -> dict:
    """Return the first Alexa response body, or {} when absent.

    simulate-skill exposes the skill's full response under
    ``skillExecutionInfo.invocations[*].invocationResponse.body.response``
    (directives, outputSpeech, reprompt, shouldEndSession).
    """
    for body in _iter_response_bodies(skill_response):
        return body
    return {}


def _output_speech_text(output_speech: object) -> str:
    """Best text of an outputSpeech object: SSML markup or plain text."""
    if isinstance(output_speech, dict):
        return (output_speech.get("ssml") or output_speech.get("text") or "")
    return ""


def _simulate_with_invocation_retry(
    client: SmapiClient, utterance: str, expected_intent: str, needs_body: bool
) -> dict:
    """Simulate once, retrying when the skill resolved the intent but was
    NOT invoked (empty skillExecutionInfo).

    An intermittent simulation-side throttle artifact under sustained SMAPI
    traffic (live 2026-09-07: consecutive no-invocation simulations for
    'voglio guardare il film ada' inside a ~35s window that the next test's
    retry escaped; ~1-2 tests per full run). 3 attempts with a settle pause
    cover the observed window; a genuinely broken endpoint fails all
    attempts, so this smooths infra flake only.
    """
    response = client.simulate(utterance)
    attempts = 1
    while (
        needs_body
        and attempts < 3
        and not _extract_response_body(_extract_skill_response(response))
    ):
        logger.warning(
            "  Skill not invoked for '%s' despite intent %s; "
            "retrying (attempt %d/3)",
            utterance, expected_intent, attempts + 1,
        )
        time.sleep(3.0)
        response = client.simulate(utterance)
        attempts += 1
    return response


def _assert_slots(
    utterance: str,
    locale: str,
    resolved_slots: dict,
    expected_slots: dict,
) -> None:
    """Assert every expected slot resolved; ``{}`` means any non-empty value."""
    for slot_name, expected_val in expected_slots.items():
        assert slot_name in resolved_slots, (
            f"Missing slot '{slot_name}' for '{utterance}' ({locale}):\n"
            f"  expected: {sorted(expected_slots.keys())}\n"
            f"  resolved: {sorted(resolved_slots.keys())}"
        )

        # {} means "any non-empty value": catch unfilled slots
        if isinstance(expected_val, dict) and not expected_val:
            resolved_val = resolved_slots[slot_name].get("value", "")
            assert resolved_val, (
                f"Slot '{slot_name}' resolved empty for '{utterance}' ({locale}):\n"
                f"  NLU matched intent but did not fill the slot"
            )


def _assert_response_markers(
    utterance: str, locale: str, skill_response: dict, fixture: dict
) -> None:
    """Assert the behavior-marker keys (JF-510): reprompt, session end,
    speech substring, and stream-URL substring.

    These read the invocation response body simulate-skill returns, which the
    older ``_assert_response_type`` paths cannot see (they only walk the
    ``responses``/``invocationResponse`` shapes, absent in current SMAPI).
    """
    if not any(k in fixture for k in RESPONSE_MARKER_KEYS):
        return

    body = _extract_response_body(skill_response)
    speech = _output_speech_text(body.get("outputSpeech"))

    expected_speech = fixture.get("expected_speech_contains", "")
    if expected_speech:
        assert expected_speech in speech, (
            f"Expected speech containing {expected_speech!r} for '{utterance}' "
            f"({locale}), but speech was: {speech[:200]!r}"
        )

    if fixture.get("expected_reprompt"):
        reprompt = body.get("reprompt")
        rep_speech = _output_speech_text(
            reprompt.get("outputSpeech") if isinstance(reprompt, dict) else None
        )
        assert rep_speech, (
            f"Expected a reprompt for '{utterance}' ({locale}), "
            f"but none was present (body keys: {sorted(body.keys())})"
        )
        # A reprompt only does its job on an open session: an explicit
        # shouldEndSession=true would close it before the reprompt window.
        assert body.get("shouldEndSession") is not True, (
            f"Reprompt present but shouldEndSession=true for '{utterance}' "
            f"({locale}): the session closes and the reprompt can never fire"
        )

    if "expected_end_session" in fixture:
        expected_end = fixture["expected_end_session"]
        actual_end = body.get("shouldEndSession")
        # An absent shouldEndSession means the session stays open (false).
        actual_open = actual_end is not True
        assert actual_open == (not expected_end), (
            f"Expected shouldEndSession={expected_end} for '{utterance}' "
            f"({locale}), but it was {actual_end!r} "
            f"(None/False both mean the session stays open)"
        )

    expected_url = fixture.get("expected_stream_url_contains", "")
    if expected_url:
        urls: list[str] = []
        for d in body.get("directives", []):
            if not isinstance(d, dict):
                continue
            dtype = d.get("type", "")
            if dtype == "AudioPlayer.Play":
                urls.append(
                    d.get("audioItem", {}).get("stream", {}).get("url", "")
                )
            elif dtype == "VideoApp.Launch":
                urls.append(d.get("video", {}).get("source", ""))
        matching = [u for u in urls if expected_url in u]
        assert matching, (
            f"Expected a stream URL containing {expected_url!r} for '{utterance}' "
            f"({locale}), but the directive URLs were: {urls[:3]}"
        )


def _assert_response_type(
    utterance: str,
    locale: str,
    skill_response: dict,
    expected_type: str,
    fixture: dict,
) -> None:
    """Validate the response matches the expected type."""
    if expected_type == "any":
        return

    directives, has_speech = _parse_skill_response(skill_response)

    if expected_type == "directive":
        expected_directive = fixture.get("expected_directive_type", "")
        matching = [d for d in directives if expected_directive in d]
        assert matching, (
            f"Expected directive '{expected_directive}' for '{utterance}' ({locale}), "
            f"but directives were: {directives[:3]}"
        )

    elif expected_type == "speech":
        assert has_speech, (
            f"Expected output speech for '{utterance}' ({locale}), "
            f"but none found in response"
        )


def _iter_response_bodies(skill_response: dict):
    """Yield every response body dict found in the skill response.

    Covers the three shapes SMAPI has exposed across its history: the
    ``responses`` list, the bare ``invocationResponse.body``, and the
    current ``invocations[*].invocationResponse.body.response`` (the only
    one present in live simulate-skill output today, JF-510).
    """
    for resp in skill_response.get("responses", []):
        body = resp.get("response", {})
        if isinstance(body, dict):
            yield body

    body = skill_response.get("invocationResponse", {}).get("body", {})
    if isinstance(body, dict):
        resp = body.get("response")
        if isinstance(resp, dict):
            yield resp
        elif body.get("directives") is not None or body.get("outputSpeech"):
            yield body

    for inv in skill_response.get("invocations", []):
        body = inv.get("invocationResponse", {}).get("body", {})
        resp = body.get("response") if isinstance(body, dict) else None
        if isinstance(resp, dict):
            yield resp


def _parse_skill_response(skill_response: dict) -> tuple[list[str], bool]:
    """Extract directive types and speech presence from the skill response.

    Walks the nested response structure once, collecting both directive
    type names and whether outputSpeech is present.
    """
    directives: list[str] = []
    has_speech = False

    for resp_body in _iter_response_bodies(skill_response):
        for d in resp_body.get("directives", []):
            directives.append(d.get("type", str(d)) if isinstance(d, dict) else str(d))
        if resp_body.get("outputSpeech"):
            has_speech = True

    return directives, has_speech


def _extract_ssml_outputs(skill_response: dict):
    """Yield each SSML outputSpeech string found in the skill response."""
    for resp_body in _iter_response_bodies(skill_response):
        os = resp_body.get("outputSpeech")
        if isinstance(os, dict) and os.get("type") == "SSML" and os.get("ssml"):
            yield os["ssml"]


def _assert_ssml_valid(skill_response: dict, utterance: str, locale: str) -> None:
    """Every SSML output speech must parse as well-formed XML.

    Catches the reserved-char crash class (a name with raw & < > yields invalid
    SSML -> Alexa InvalidResponse), e.g. JF-323. Plain-text output is skipped.
    """
    for ssml in _extract_ssml_outputs(skill_response):
        try:
            ET.fromstring(ssml)
        except ET.ParseError as exc:
            pytest.fail(
                f"Invalid SSML for '{utterance}' ({locale}): {exc}\n  ssml: {ssml[:200]}"
            )


def _extract_apl_directives(skill_response: dict) -> list[dict]:
    """Extract all APL RenderDocument directives from the skill response."""
    apl_directives = []

    for resp_body in _iter_response_bodies(skill_response):
        for d in resp_body.get("directives", []):
            if isinstance(d, dict) and "APL" in d.get("type", ""):
                apl_directives.append(d)

    return apl_directives


def _validate_apl_directive(directive: dict) -> list[str]:
    """Validate an APL RenderDocument directive. Returns list of errors."""
    errors = []
    dtype = directive.get("type", "")

    if dtype == "Alexa.Presentation.APL.RenderDocument":
        doc = directive.get("document")
        ds = directive.get("datasources")

        if not doc:
            errors.append("APL RenderDocument missing 'document'")
        else:
            if doc.get("type") != "APL":
                errors.append(f"APL document type is {doc.get('type')!r}, expected 'APL'")
            mt = doc.get("mainTemplate", {})
            if "parameters" not in mt:
                errors.append("APL mainTemplate missing 'parameters' — datasource binding broken")
            if "items" not in mt:
                errors.append("APL mainTemplate missing 'items'")

        if not ds:
            errors.append("APL RenderDocument missing 'datasources' — nothing to render")
        elif not isinstance(ds, dict) or not ds:
            errors.append("APL datasources is empty or not an object")
        else:
            for ds_name, ds_val in ds.items():
                if not isinstance(ds_val, dict):
                    errors.append(f"APL datasources.{ds_name} is not an object")
                elif ds_val.get("type") != "object":
                    errors.append(f"APL datasources.{ds_name}.type should be 'object', got {ds_val.get('type')!r}")
                elif "properties" not in ds_val:
                    errors.append(f"APL datasources.{ds_name} missing 'properties'")

    return errors


def _check_side_effects(
    intent: str,
    jellyfin_client,
) -> None:
    """Check Jellyfin side effects based on intent type."""
    playback_intents = {
        "PlayMoodMusicIntent", "PlayRandomIntent", "PlayLastAddedIntent",
        "PlaySongIntent", "PlayAlbumIntent", "PlayArtistSongsIntent",
        "PlayByGenreIntent", "PlayPlaylistIntent", "PlayFavoritesIntent",
        "ContinueWatchingIntent", "PlayEpisodeIntent", "PlayChannelIntent",
    }

    if intent in playback_intents:
        now_playing = jellyfin_client.get_now_playing()
        if not now_playing:
            logger.warning(
                "  No active playback detected for %s (may be delayed)",
                intent,
            )
        else:
            logger.info(
                "  Side-effect OK: %s is playing '%s'",
                intent, now_playing.get("Name", "?"),
            )


# ---------------------------------------------------------------------------
# Reliability E2E test — runs each intent multiple times to catch hangs
# ---------------------------------------------------------------------------


@pytest.fixture
def reliability_fixture(request):
    """Indirect fixture: each parametrized case from e2e_reliability_*.yaml."""
    return request.param


@pytest.fixture
def reliability_smapi_client(skill_id, smapi_delay, reliability_fixture):
    """Per-locale SmapiClient for reliability tests."""
    return SmapiClient(
        skill_id=skill_id,
        locale=reliability_fixture["locale"],
        delay=smapi_delay,
        invocation_name=reliability_fixture.get("invocation_name", ""),
    )


@pytest.fixture
def reliability_session_reset(skill_id, smapi_delay):
    """Best-effort bare-'stop' reset of the persistent simulate-skill session.

    Needed BETWEEN reliability iterations since JF-488: the pause response now
    keeps the session open (reprompt + shouldEndSession=false), and the NEXT
    prefixed one-shot uttered into that open session routes to
    AMAZON.FallbackIntent instead of its intent (evidenced live 2026-09-06:
    prefixed 'pausa' x2 without a reset alternated Pause/Fallback, 2/3
    reliability passes). A bare 'stop' closes the open session; when no session
    is open the simulation errors, which is the benign no-op case (same
    best-effort contract as the per-test reset fixture above).
    """
    reset_client = SmapiClient(
        skill_id=skill_id,
        locale="it-IT",
        delay=smapi_delay,
        invocation_name="",
    )

    def reset() -> None:
        try:
            reset_client.simulate("stop")
        except Exception as exc:  # noqa: BLE001 - reset is best effort
            logger.debug("Reliability inter-iteration reset failed: %s", exc)

    return reset


_MAX_PER_ITERATION_S = 30.0


@pytest.mark.e2e
def test_e2e_reliability(
    dry_run, reliability_fixture, reliability_smapi_client,
    reliability_session_reset, jellyfin_client,
):
    """Run each intent multiple times, tracking timing to detect intermittent hangs.

    Each fixture case can specify an ``iterations`` count (default 3).
    Fails if any iteration exceeds the per-iteration timeout or SMAPI fails.
    """
    utterance = reliability_fixture["utterance"]
    expected_intent = reliability_fixture["expected_intent"]
    locale = reliability_fixture["locale"]
    iterations = reliability_fixture.get("iterations", 3)

    if dry_run:
        assert utterance, f"Empty utterance in {reliability_fixture.get('source', '?')}"
        pytest.skip("dry-run mode: reliability simulation skipped")

    logger.info(
        "RELIABILITY [%s] (%s) x%d iterations, expecting %s",
        utterance, locale, iterations, expected_intent,
    )

    timings: list[float] = []
    failures: list[str] = []

    for i in range(iterations):
        label = f"iter {i + 1}/{iterations}"
        start = time.monotonic()

        try:
            response = reliability_smapi_client.simulate(utterance)
        except SmapiError as exc:
            elapsed = time.monotonic() - start
            failures.append(f"{label}: SMAPI error after {elapsed:.1f}s — {exc}")
            logger.error("  %s FAILED: %s", label, exc)
            continue

        elapsed = time.monotonic() - start
        timings.append(elapsed)

        # Parse NLU result
        result = SmapiClient.parse_nlu_result(response)
        resolved_intent = result["intent"]

        if resolved_intent != expected_intent:
            failures.append(
                f"{label}: intent mismatch (expected {expected_intent}, got {resolved_intent})"
            )
            logger.error(
                "  %s INTENT MISMATCH: expected=%s got=%s (%.1fs)",
                label, expected_intent, resolved_intent, elapsed,
            )
        elif elapsed > _MAX_PER_ITERATION_S:
            failures.append(f"{label}: slow response ({elapsed:.1f}s > {_MAX_PER_ITERATION_S}s)")
            logger.warning("  %s SLOW: %.1fs", label, elapsed)
        else:
            logger.info("  %s OK (%.1fs) -> %s", label, elapsed, resolved_intent)

        # Stop playback between iterations to avoid state leakage
        if jellyfin_client is not None:
            try:
                jellyfin_client.stop_playback()
            except Exception:  # noqa: BLE001
                pass

        # Close any session the response left open (JF-488 pause keeps it
        # open) so the next iteration is a clean one-shot, not a polluted
        # in-session capture (see reliability_session_reset docstring).
        # CONDITIONAL: each reset costs a full SMAPI simulation (~10s), and
        # resetting unconditionally pushed the 5-iteration tests past the
        # per-test timeout alarm (live 2026-09-07: 'riproduci i miei
        # preferiti' hit _TimeoutError at 120s). Play responses end the
        # session themselves, so only an actually-open session pays the reset.
        if i < iterations - 1:
            body = _extract_response_body(_extract_skill_response(response))
            if body.get("shouldEndSession") is not True:
                reliability_session_reset()

    # --- Summary ---
    avg_time = sum(timings) / len(timings) if timings else 0.0
    max_time = max(timings) if timings else 0.0
    logger.info(
        "RELIABILITY SUMMARY [%s]: %d/%d passed, avg=%.1fs, max=%.1fs",
        utterance, iterations - len(failures), iterations, avg_time, max_time,
    )

    assert not failures, (
        f"Reliability failures for '{utterance}' ({locale}) over {iterations} iterations:\n"
        + "\n".join(f"  - {f}" for f in failures)
    )


# ---------------------------------------------------------------------------
# Fast mode E2E test — exercises the Fast SearchResponseMode code path
# ---------------------------------------------------------------------------


_FAST_MODE_ARTIST_UTTERANCES = [
    # pytest.param(utterance, locale, invocation_name)
    pytest.param(
        "metti una canzone dei soul coughing", "it-IT", "mia collezione",
        id="fast-metti una canzone dei soul coug",
    ),
    # JF-508 family (2026-09-06, profile-nlu clean-string evidence):
    # 'metti una canzone dei xyzzyfoo' now selects PlaySongIntent (song
    # absorbs 'una canzone dei xyzzyfoo' wholesale) for out-of-catalog
    # artists; the Fast-mode code path itself is unchanged and covered by
    # the in-catalog entry above. Re-enable when the model routing is fixed.
    pytest.param(
        "metti una canzone dei xyzzyfoo", "it-IT", "mia collezione",
        marks=pytest.mark.skip(
            reason="JF-508: PlaySongIntent absorbs the artist carrier for "
            "out-of-catalog names (routing regression, not a Fast-mode bug)"
        ),
        id="fast-metti una canzone dei xyzzyfoo",
    ),
]


@pytest.mark.e2e
@pytest.mark.parametrize(
    "utterance,locale,invocation_name",
    _FAST_MODE_ARTIST_UTTERANCES,
)
def test_e2e_fast_mode(
    dry_run,
    utterance: str,
    locale: str,
    invocation_name: str,
    skill_id: str,
    smapi_delay: float,
    jellyfin_client,
):
    """Toggle user to Fast mode, run an artist query, verify it resolves.

    This exercises the Fast-mode code paths:
    - In-memory: tier 1 only, then tier 4 (fuzzy all) on miss — skips tiers 2-3
    - DB fallback: single SearchTerm query, no ASR variants, no fallback tiers
    - Disambiguation: auto-play best match instead of "Did you mean?"

    After the test, resets the user back to Thorough mode (the global default).
    """
    if dry_run:
        pytest.skip("dry-run mode: Fast mode E2E test skipped")

    if jellyfin_client is None:
        pytest.skip("Jellyfin E2E parameters not configured")

    # Switch user to Fast mode
    logger.info("FAST MODE: setting user %s to Fast mode", jellyfin_client.user_id)
    jellyfin_client.set_search_mode("Fast")

    try:
        client = SmapiClient(
            skill_id=skill_id,
            locale=locale,
            delay=smapi_delay,
            invocation_name=invocation_name,
        )

        logger.info("FAST MODE [%s] (%s)", utterance, locale)
        response = client.simulate(utterance)

        result = SmapiClient.parse_nlu_result(response)
        resolved_intent = result["intent"]

        assert resolved_intent == "PlayArtistSongsIntent", (
            f"Intent mismatch for '{utterance}' ({locale}) in Fast mode:\n"
            f"  expected: PlayArtistSongsIntent\n"
            f"  actual:   {resolved_intent}"
        )

        logger.info("  FAST MODE PASS: [%s] -> %s", utterance[:30], resolved_intent)

    finally:
        # Always reset back to global default (null = use global default)
        logger.info("FAST MODE: resetting user %s to global default", jellyfin_client.user_id)
        jellyfin_client.set_search_mode(None)


# ---------------------------------------------------------------------------
# Smoke E2E tests (JF-511 option b): two-step shape for the non-it locales
# ---------------------------------------------------------------------------


@pytest.fixture
def smoke_fixture(request):
    """Indirect fixture: each parametrized case from e2e_smoke_*.yaml."""
    return request.param


def _assert_play_or_gate_tell(
    utterance: str, locale: str, skill_response: dict, gate_substring: str
) -> None:
    """Random-play disjunction: AudioPlayer.Play on /Audio/ OR the localized
    VideoRequiresScreen gate Tell.

    For locales where the MediaType slot does not fill in-session (de-DE,
    live evidence 2026-09-07: 'spiele zufällige musik' routes to
    PlayRandomIntent with media_type='') the handler cannot be pinned to
    audio kinds, so the honest pin is: the skill answered with a real play
    or with the correct screen-gate Tell, and either way the session ends.
    """
    body = _extract_response_body(skill_response)
    assert body.get("shouldEndSession") is True, (
        f"Expected the session to end for '{utterance}' ({locale}), "
        f"but shouldEndSession was {body.get('shouldEndSession')!r}"
    )
    play_urls = [
        d.get("audioItem", {}).get("stream", {}).get("url", "")
        for d in body.get("directives", [])
        if isinstance(d, dict) and d.get("type") == "AudioPlayer.Play"
    ]
    speech = _output_speech_text(body.get("outputSpeech"))
    played = any("/Audio/" in u for u in play_urls)
    gated = gate_substring in speech
    assert played or gated, (
        f"Expected a music play or the '{gate_substring}' gate Tell for "
        f"'{utterance}' ({locale}), but directives were "
        f"{[d.get('type') for d in body.get('directives', [])]} and speech "
        f"was {speech[:160]!r}"
    )


@pytest.mark.e2e
def test_e2e_smoke_two_step(
    dry_run, smoke_fixture, skill_id, smapi_delay
):
    """Two-step smoke: open the skill in the locale, then a bare command.

    Why two steps (JF-511 measurement, 2026-09-07): the invocation-prefixed
    one-shot composition is dead in the de/fr/es marketplaces (0/24 across
    grammatical forms) while a bare in-session command after opening the
    skill works. The open verb per locale is fixture data (open_utterance,
    verified live per locale before authoring: 'launch' in en-US/en-IN and
    fr-CA, where 'open' and 'ouvre' do not resolve).

    Per test: (optional conditional bare-'stop' close) -> open -> bare
    command -> intent/slot/response assertions. The open and the command
    both cost a full simulation (~13s + ~7s), so the whole test stays well
    inside the per-test SMAPI timeout.
    """
    fixture = smoke_fixture
    utterance = fixture["utterance"]
    expected_intent = fixture["expected_intent"]
    expected_slots = fixture.get("expected_slots", {})
    expected_response_type = fixture.get("expected_response_type", "any")
    skip_reason = fixture.get("skip_reason", "")
    locale = fixture["locale"]
    open_utterance = fixture["open_utterance"]

    if dry_run:
        assert utterance, f"Empty utterance in {fixture.get('source', '?')}"
        assert open_utterance, (
            f"Missing open_utterance for the smoke file of {locale} "
            f"({fixture.get('source', '?')})"
        )
        assert expected_intent, f"Missing expected_intent for '{utterance}'"
        assert expected_response_type in ("any", "speech", "directive"), (
            f"Invalid expected_response_type: {expected_response_type}"
        )
        if expected_response_type == "directive":
            assert fixture.get("expected_directive_type"), (
                f"expected_directive_type required when response_type is "
                f"'directive' for '{utterance}'"
            )
        if "expected_end_session" in fixture:
            assert isinstance(fixture["expected_end_session"], bool), (
                f"expected_end_session must be a bool for '{utterance}'"
            )
        for str_key in (
            "expected_speech_contains",
            "expected_stream_url_contains",
            "expected_play_or_gate_tell",
        ):
            val = fixture.get(str_key, "")
            assert not val or isinstance(val, str), (
                f"{str_key} must be a non-empty string or omitted for '{utterance}'"
            )
        if skip_reason:
            assert isinstance(skip_reason, str) and skip_reason.strip(), (
                f"skip_reason must be a non-empty string for '{utterance}'"
            )
        pytest.skip("dry-run mode: E2E simulation skipped")

    # A tracked, documented non-passing state (known model regression,
    # platform limitation): keep the fixture visible, record why it is not
    # asserted today. Skipping costs no SMAPI call.
    if skip_reason:
        pytest.skip(f"'{utterance}' ({locale}): {skip_reason}")

    client = SmapiClient(
        skill_id=skill_id, locale=locale, delay=smapi_delay, invocation_name=""
    )

    # Close any session the previous test (or an earlier run) left open in
    # this locale: an open dialog would capture the open utterance into its
    # elicited slot. Same conditional policy as the one-shot autouse reset
    # (first test in a locale always resets; afterwards only when tracked
    # open, and the previous command normally ends the session itself, so
    # this is usually free).
    _ensure_session_closed(skill_id, locale, smapi_delay)

    # --- Step 1: open the skill (the locale's invocation + open verb) ---
    # One retry on a transient simulation error (the en-GB class: platform
    # 'unexpected error' after ~21s, observed about once per ten opens,
    # JF-511); a genuinely broken open shape fails on the retry too.
    logger.info("SMOKE OPEN [%s] (%s)", open_utterance, locale)
    try:
        try:
            open_response = client.simulate(open_utterance)
        except SmapiError:
            time.sleep(3.0)
            open_response = client.simulate(open_utterance)
    except SmapiError as exc:
        pytest.fail(
            f"Open simulation error for '{open_utterance}' ({locale}): {exc}"
        )
    # An open that did not invoke the skill leaves nothing for the command
    # to ride on: fail here, with the considered intents, rather than as a
    # confusing command-intent mismatch below.
    considered = [
        c.get("name")
        for c in open_response.get("result", {})
        .get("alexaExecutionInfo", {})
        .get("consideredIntents", [])
    ][:3]
    assert _extract_response_body(_extract_skill_response(open_response)), (
        f"Open utterance '{open_utterance}' ({locale}) did not invoke the "
        f"skill (considered: {considered})"
    )

    # --- Step 2: the bare in-session command ---
    logger.info(
        "SMOKE CMD [%s] (%s) expecting %s (response: %s)",
        utterance, locale, expected_intent, expected_response_type,
    )
    needs_body = (
        expected_response_type != "any"
        or any(k in fixture for k in RESPONSE_MARKER_KEYS)
    )
    try:
        response = _simulate_with_invocation_retry(
            client, utterance, expected_intent, needs_body
        )
    except SmapiError as exc:
        _open_sessions.add(locale)
        pytest.fail(f"SMAPI simulation error for '{utterance}' ({locale}): {exc}")

    _record_session_state(locale, response)

    result = SmapiClient.parse_nlu_result(response)
    resolved_intent = result["intent"]

    assert resolved_intent == expected_intent, (
        f"Intent mismatch for '{utterance}' ({locale}, in-session after "
        f"'{open_utterance}'):\n"
        f"  expected: {expected_intent}\n"
        f"  actual:   {resolved_intent}"
    )

    _assert_slots(utterance, locale, result["slots"], expected_slots)

    skill_response = _extract_skill_response(response)
    _assert_response_type(
        utterance, locale, skill_response, expected_response_type, fixture
    )
    _assert_response_markers(utterance, locale, skill_response, fixture)
    gate_substring = fixture.get("expected_play_or_gate_tell", "")
    if gate_substring:
        _assert_play_or_gate_tell(
            utterance, locale, skill_response, gate_substring
        )
    _assert_ssml_valid(skill_response, utterance, locale)

    logger.info("  SMOKE PASS: [%s] -> %s", utterance[:30], resolved_intent)

    # If the command left the skill session open (reprompt/disambiguation),
    # close it now so the next test's open starts clean; costs a simulation
    # only when actually needed.
    if locale in _open_sessions:
        _bare_stop(skill_id, locale, smapi_delay)
        _open_sessions.discard(locale)
