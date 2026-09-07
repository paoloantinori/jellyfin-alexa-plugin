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


@pytest.fixture(autouse=True)
def _reset_simulation_session(dry_run, skill_id, smapi_delay):
    """End the persistent simulate-skill session before each E2E test.

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
    Known limitation: the reset hardcodes locale it-IT. The e2e en-US fixtures run
    without a locale-scoped reset (the it-IT reset fires before them and fails
    benignly); if a non-it-IT fixture opens a FindSong-style dialog, extend here with
    a locale-scoped reset (cf. JF-400/JF-414; comment corrected 2026-09-07, the old
    'every fixture is it-IT' premise had been false since e2e_en-US landed in May).

    No-op in dry-run mode: this autouse fixture fires BEFORE each test's own
    dry-run skip, so an unguarded simulate() here issued one real
    ``ask smapi simulate-skill`` subprocess per collected test (JF-435).
    """
    if dry_run:
        return

    reset_client = SmapiClient(
        skill_id=skill_id,
        locale="it-IT",
        delay=smapi_delay,
        invocation_name="",
    )
    try:
        reset_client.simulate("stop")
    except Exception as exc:  # noqa: BLE001 - reset is best effort
        logger.warning("Session reset simulation failed (continuing): %s", exc)


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
        response = e2e_smapi_client.simulate(utterance)
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
            response = e2e_smapi_client.simulate(utterance)
            attempts += 1
    except SmapiError as exc:
        pytest.fail(f"SMAPI simulation error for '{utterance}' ({locale}): {exc}")

    # --- Parse NLU result ---
    result = SmapiClient.parse_nlu_result(response)
    resolved_intent = result["intent"]

    assert resolved_intent == expected_intent, (
        f"Intent mismatch for '{utterance}' ({locale}):\n"
        f"  expected: {expected_intent}\n"
        f"  actual:   {resolved_intent}"
    )

    # --- Slot assertions ---
    resolved_slots = result["slots"]
    for slot_name, expected_val in expected_slots.items():
        assert slot_name in resolved_slots, (
            f"Missing slot '{slot_name}' for '{utterance}' ({locale}):\n"
            f"  expected: {sorted(expected_slots.keys())}\n"
            f"  resolved: {sorted(resolved_slots.keys())}"
        )

        # {} means "any non-empty value" — catch unfilled slots
        if isinstance(expected_val, dict) and not expected_val:
            resolved_val = resolved_slots[slot_name].get("value", "")
            assert resolved_val, (
                f"Slot '{slot_name}' resolved empty for '{utterance}' ({locale}):\n"
                f"  NLU matched intent but did not fill the slot"
            )

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
