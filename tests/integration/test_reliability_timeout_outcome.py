"""Unit tests for resolve_reliability_timeout_outcome (JF-893).

Pure decision function, no SMAPI, no network. Run standalone:

    tests/integration/.venv/bin/python -m pytest test_reliability_timeout_outcome.py -v
"""

from test_e2e import resolve_reliability_timeout_outcome


def test_timeout_after_correct_iteration_returns_skip_reason():
    reason = resolve_reliability_timeout_outcome(1, 5, "riproduci i miei preferiti")
    assert reason is not None
    assert "simulate service slow" in reason
    assert "1/5" in reason


def test_timeout_with_zero_correct_iterations_returns_none():
    # A real failure signal: must NOT be softened into a skip.
    assert resolve_reliability_timeout_outcome(0, 5, "riproduci i miei preferiti") is None


def test_timeout_with_recorded_failures_returns_none():
    # A recorded intent mismatch must not be wiped by a skip (code review):
    # the timeout surfaces as a failure carrying the evidence.
    assert (
        resolve_reliability_timeout_outcome(
            1, 5, "riproduci i miei preferiti",
            recorded_failures=["iter 2/5: intent mismatch (expected PlayFavoritesIntent, got PlaySongIntent)"],
        )
        is None
    )


def test_timeout_with_empty_failures_still_skips():
    assert (
        resolve_reliability_timeout_outcome(2, 5, "riproduci i miei preferiti", [])
        is not None
    )


def test_multiple_correct_iterations_reported_in_reason():
    reason = resolve_reliability_timeout_outcome(3, 5, "apri la mia collezione")
    assert reason is not None
    assert "3/5" in reason
