"""Unit tests for resolve_reliability_timeout_outcome (JF-893).

Pure decision function, no SMAPI, no network. Run standalone:

    tests/integration/.venv/bin/python -m pytest test_reliability_timeout_outcome.py -v
"""

from test_e2e import decide_reliability_timeout_exit, resolve_reliability_timeout_outcome


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


# ---------------------------------------------------------------------------
# JF-897: the verdict mapping both alarm landing sites share
# (decide_reliability_timeout_exit). The alarm's scope is the whole test,
# so a fire in the NON-simulate phases (inter-iteration stop/reset, the
# SMAPI_DELAY sleep, the summary) must take the same decision as the
# JF-893 simulate-call fire.
# ---------------------------------------------------------------------------


def test_alarm_in_nonsimulate_phase_after_correct_iteration_skips():
    # Alarm fired during the post-loop summary/reset with one proven
    # iteration and a clean failure list: skip, reason carried.
    verdict, reason = decide_reliability_timeout_exit(1, 5, "riproduci i miei preferiti", [])
    assert verdict == "skip"
    assert reason is not None
    assert "1/5" in reason


def test_alarm_in_nonsimulate_phase_with_recorded_failures_fails():
    # An intent mismatch recorded before the alarm must surface as FAIL,
    # never wiped by a skip, in every phase.
    verdict, reason = decide_reliability_timeout_exit(
        1, 5, "riproduci i miei preferiti",
        ["iter 2/5: intent mismatch (expected PlayFavoritesIntent, got PlaySongIntent)"],
    )
    assert verdict == "fail"
    assert reason is None


def test_alarm_in_nonsimulate_phase_with_slow_response_failure_fails():
    # A slow-response row lands in failures (and counts as correctly
    # resolved): the recorded failure wins over the skip.
    verdict, reason = decide_reliability_timeout_exit(
        1, 5, "riproduci i miei preferiti",
        ["iter 1/5: slow response (44.0s > 30.0s)"],
    )
    assert verdict == "fail"
    assert reason is None


def test_alarm_in_nonsimulate_phase_zero_correct_reraises():
    # Zero proven iterations in any phase is a real failure signal: the
    # conftest fixture's raw FAIL is the most informative exit.
    verdict, reason = decide_reliability_timeout_exit(0, 5, "riproduci i miei preferiti", [])
    assert verdict == "reraise"
    assert reason is None


def test_alarm_during_first_simulate_call_still_reraises():
    # The JF-893 shape unchanged: the alarm hits the simulate call itself
    # on iteration 1, nothing proven, nothing recorded.
    verdict, reason = decide_reliability_timeout_exit(0, 3, "apri la mia collezione")
    assert verdict == "reraise"
    assert reason is None
