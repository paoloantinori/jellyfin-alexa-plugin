"""Unit tests for the FAILED-simulation classifier (JF-899).

SmapiClient._failed_simulation_error is the pure decision seam that maps
a platform cause message onto the typed SmapiError siblings: no SMAPI, no
network, no subprocess. Run standalone:

    tests/integration/.venv/bin/python -m pytest test_smapi_failure_classification.py -v
"""

from smapi_client import (
    SmapiClient,
    SmapiError,
    SmapiSelectionLostError,
    SmapiUnexpectedError,
)


def _classify(error_msg: str, utterance: str = "ouvre mon serveur",
              detail: str = "") -> Exception:
    return SmapiClient._failed_simulation_error(
        error_msg, utterance, "fr-FR", detail
    )


def test_selection_lost_platform_message_classifies():
    # The live 2026-10-15 probe text (research_smapi-endpoints, b61f4853).
    exc = _classify(
        "This utterance did not resolve to any intent in your skill",
        detail="after 12.3s ",
    )
    assert type(exc) is SmapiSelectionLostError
    # The composed message still carries the evidence for the report.
    assert "ouvre mon serveur" in str(exc)
    assert "fr-FR" in str(exc)


def test_selection_lost_truncated_probe_form_classifies():
    # The 2026-09-22 research recorded the message truncated one word
    # earlier; the marker is the stable shared prefix.
    exc = _classify("This utterance did not resolve to any intent")
    assert type(exc) is SmapiSelectionLostError


def test_classification_is_case_insensitive():
    exc = _classify("This Utterance DID NOT RESOLVE to any intent")
    assert type(exc) is SmapiSelectionLostError


def test_fast_fail_and_poll_details_classify_alike():
    # simulate() reaches the classifier with detail="" on the initiation
    # fast-fail and "after Ns " from the poll loop; the class must not
    # depend on the path.
    fast = _classify(
        "utterance did not resolve to any intent in your skill", detail=""
    )
    polled = _classify(
        "utterance did not resolve to any intent in your skill",
        detail="after 9.1s ",
    )
    assert type(fast) is type(polled) is SmapiSelectionLostError


def test_outage_message_still_classifies_unexpected():
    exc = _classify("An unexpected error occurred")
    assert type(exc) is SmapiUnexpectedError


def test_both_markers_classify_selection_lost():
    # Precedence: the specific selection-loss marker wins over the generic
    # outage catch-all, so a composed cause text takes the per-call class
    # (the smoke open falls back WITHOUT flagging the locale) rather than
    # the chronic outage class (which flags the locale for the run).
    exc = _classify(
        "This utterance did not resolve to any intent in your skill: "
        "an unexpected error occurred"
    )
    assert type(exc) is SmapiSelectionLostError


def test_unknown_message_stays_plain_smapi_error():
    # Exact type: a plain SmapiError is neither fallback class, so the
    # smoke open hard-fails on it (JF-899 keeps the hard failure for
    # genuinely different error shapes).
    exc = _classify("The device is offline")
    assert type(exc) is SmapiError


def test_empty_message_stays_plain_smapi_error():
    # A FAILED result with no error.message must not pattern-match anything.
    assert type(_classify("")) is SmapiError


def test_marker_in_utterance_text_never_classifies():
    # Documented invariant: the markers are checked against the platform
    # cause field alone, because the composed message embeds the
    # utterance, whose text must never trigger a classification.
    exc = _classify(
        "The device is offline",
        utterance="this utterance did not resolve to any intent",
    )
    assert type(exc) is SmapiError


def test_selection_lost_is_catchable_as_smapi_error():
    # The property that keeps the command steps red: every generic
    # `except SmapiError` handler (smoke step 2, the e2e command catch,
    # the reliability loop) still catches the new sibling, so only the
    # smoke OPEN step, which lists the type explicitly, falls back.
    assert issubclass(SmapiSelectionLostError, SmapiError)


def test_selection_lost_is_not_the_outage_class():
    # Siblings, not nested: the smoke open step tells the two fallback
    # classes apart by type, and a SmapiUnexpectedError catch must never
    # swallow a selection loss (it flags the locale; the selection loss
    # must not).
    assert not issubclass(SmapiSelectionLostError, SmapiUnexpectedError)
    assert not issubclass(SmapiUnexpectedError, SmapiSelectionLostError)
