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


@pytest.mark.e2e
def test_e2e_full_chain(request, e2e_fixture, e2e_smapi_client, jellyfin_client):
    """Full-chain test: utterance -> NLU -> skill -> response + side effects."""
    utterance = e2e_fixture["utterance"]
    expected_intent = e2e_fixture["expected_intent"]
    expected_slots = e2e_fixture.get("expected_slots", {})
    expected_response_type = e2e_fixture.get("expected_response_type", "any")
    locale = e2e_fixture["locale"]

    if request.config.getoption("--dry-run"):
        assert utterance, f"Empty utterance in {e2e_fixture.get('source', '?')}"
        assert expected_intent, f"Missing expected_intent for '{utterance}'"
        assert expected_response_type in ("any", "speech", "directive"), (
            f"Invalid expected_response_type: {expected_response_type}"
        )
        if expected_response_type == "directive":
            assert e2e_fixture.get("expected_directive_type"), (
                f"expected_directive_type required when response_type is 'directive'"
            )
        pytest.skip("dry-run mode: E2E simulation skipped")

    logger.info(
        "E2E [%s] (%s) expecting %s (response: %s)",
        utterance, locale, expected_intent, expected_response_type,
    )

    # --- Run full simulation via SMAPI ---
    try:
        response = e2e_smapi_client.simulate(utterance)
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
    """Extract the skill's response from the simulation result."""
    result = simulation.get("result", {})
    alexa_info = result.get("alexaExecutionInfo", {})
    skill_info = alexa_info.get("skillExecutionInfo", {})
    return skill_info


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


def _parse_skill_response(skill_response: dict) -> tuple[list[str], bool]:
    """Extract directive types and speech presence from the skill response.

    Walks the nested response structure once, collecting both directive
    type names and whether outputSpeech is present.
    """
    directives: list[str] = []
    has_speech = False

    for resp in skill_response.get("responses", []):
        resp_body = resp.get("response", {})
        for d in resp_body.get("directives", []):
            directives.append(d.get("type", str(d)) if isinstance(d, dict) else str(d))
        if resp_body.get("outputSpeech"):
            has_speech = True

    payload = skill_response.get("invocationResponse", {})
    body = payload.get("body", {})
    if isinstance(body, dict):
        for d in body.get("directives", []):
            if isinstance(d, dict):
                t = d.get("type", "")
                if t and t not in directives:
                    directives.append(t)
        if body.get("outputSpeech"):
            has_speech = True

    return directives, has_speech


def _extract_ssml_outputs(skill_response: dict):
    """Yield each SSML outputSpeech string found in the skill response."""
    for resp in skill_response.get("responses", []):
        os = resp.get("response", {}).get("outputSpeech")
        if isinstance(os, dict) and os.get("type") == "SSML" and os.get("ssml"):
            yield os["ssml"]
    body = skill_response.get("invocationResponse", {}).get("body", {})
    if isinstance(body, dict):
        os = body.get("outputSpeech")
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

    for resp in skill_response.get("responses", []):
        resp_body = resp.get("response", {})
        for d in resp_body.get("directives", []):
            if isinstance(d, dict) and "APL" in d.get("type", ""):
                apl_directives.append(d)

    payload = skill_response.get("invocationResponse", {})
    body = payload.get("body", {})
    if isinstance(body, dict):
        for d in body.get("directives", []):
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


_MAX_PER_ITERATION_S = 30.0


@pytest.mark.e2e
def test_e2e_reliability(request, reliability_fixture, reliability_smapi_client, jellyfin_client):
    """Run each intent multiple times, tracking timing to detect intermittent hangs.

    Each fixture case can specify an ``iterations`` count (default 3).
    Fails if any iteration exceeds the per-iteration timeout or SMAPI fails.
    """
    utterance = reliability_fixture["utterance"]
    expected_intent = reliability_fixture["expected_intent"]
    locale = reliability_fixture["locale"]
    iterations = reliability_fixture.get("iterations", 3)

    if request.config.getoption("--dry-run"):
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
    # (utterance, locale, invocation_name)
    ("metti una canzone dei soul coughing", "it-IT", "mia collezione"),
    ("metti una canzone dei xyzzyfoo", "it-IT", "mia collezione"),
]


@pytest.mark.e2e
@pytest.mark.parametrize(
    "utterance,locale,invocation_name",
    _FAST_MODE_ARTIST_UTTERANCES,
    ids=[f"fast-{u[:30]}" for u, _, _ in _FAST_MODE_ARTIST_UTTERANCES],
)
def test_e2e_fast_mode(
    request,
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
    if request.config.getoption("--dry-run"):
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
