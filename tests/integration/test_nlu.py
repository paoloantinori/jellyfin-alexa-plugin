"""NLU integration tests using ASK CLI simulate-skill.

Each test case sends an utterance through Alexa's NLU pipeline and asserts
that the resolved intent and required slots match the expected values from
YAML fixture files.
"""

from __future__ import annotations

import logging

import pytest

from smapi_client import SmapiClient, SmapiError

logger = logging.getLogger("nlu.test")


@pytest.fixture
def nlu_fixture(request):
    """Indirect fixture: each parametrized case is a dict from YAML."""
    return request.param


@pytest.fixture
def smapi_client(skill_id, smapi_delay, nlu_fixture):
    """Per-locale SmapiClient instance."""
    return SmapiClient(
        skill_id=skill_id,
        locale=nlu_fixture["locale"],
        delay=smapi_delay,
        invocation_name=nlu_fixture.get("invocation_name", ""),
    )


@pytest.mark.nlu
def test_utterance_resolves_correct_intent(request, nlu_fixture, smapi_client):
    """Assert that an utterance resolves to the expected intent with required slots."""
    utterance = nlu_fixture["utterance"]
    expected_intent = nlu_fixture["expected_intent"]
    expected_slots = nlu_fixture.get("expected_slots", {})
    locale = nlu_fixture["locale"]

    if request.config.getoption("--dry-run"):
        # Fixture-only validation: check schema, skip SMAPI call
        assert utterance, f"Empty utterance in {nlu_fixture.get('source', '?')}"
        assert expected_intent, f"Missing expected_intent for '{utterance}'"
        assert isinstance(expected_slots, dict), (
            f"expected_slots must be a dict, got {type(expected_slots).__name__}"
        )
        pytest.skip("dry-run mode: SMAPI call skipped")

    logger.info("Testing [%s] (%s) expecting %s", utterance, locale, expected_intent)

    # --- Live SMAPI simulation ---
    try:
        response = smapi_client.simulate(utterance)
    except SmapiError as exc:
        pytest.fail(f"SMAPI error for '{utterance}' ({locale}): {exc}")

    result = SmapiClient.parse_nlu_result(response)

    resolved_intent = result["intent"]

    # --- Intent assertion ---
    assert resolved_intent == expected_intent, (
        f"Intent mismatch for '{utterance}' ({locale}):\n"
        f"  expected: {expected_intent}\n"
        f"  actual:   {resolved_intent}\n"
        f"  confidence: {result['confidence']:.2f}"
    )

    # --- Slot assertions ---
    resolved_slots = result["slots"]
    for slot_name in expected_slots:
        assert slot_name in resolved_slots, (
            f"Missing slot '{slot_name}' for '{utterance}' ({locale}):\n"
            f"  expected slots: {sorted(expected_slots.keys())}\n"
            f"  resolved slots: {sorted(resolved_slots.keys())}"
        )

    logger.info(
        "  PASS: [%s] -> %s (conf=%.2f, slots=%s)",
        utterance[:30], resolved_intent, result["confidence"],
        sorted(resolved_slots.keys()),
    )
