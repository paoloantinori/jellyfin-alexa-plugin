"""E2E integration tests exercising the full Alexa -> skill -> Jellyfin chain.

Uses SMAPI simulate-skill to send utterances through Alexa's full pipeline,
then validates the skill's response and optional Jellyfin side effects.

Requires:
- A running Jellyfin server accessible to the Alexa skill endpoint
- --jellyfin-url, --jellyfin-api-key, --jellyfin-user CLI options (or env vars)
"""

from __future__ import annotations

import logging

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
    for slot_name in expected_slots:
        assert slot_name in resolved_slots, (
            f"Missing slot '{slot_name}' for '{utterance}' ({locale}):\n"
            f"  expected: {sorted(expected_slots.keys())}\n"
            f"  resolved: {sorted(resolved_slots.keys())}"
        )

    # --- Response assertions ---
    skill_response = _extract_skill_response(response)
    _assert_response_type(
        utterance, locale, skill_response, expected_response_type, e2e_fixture
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
