"""Unit tests for SmapiClient.parse_profile_nlu_result's considered capture
(JF-968): fixture-based, no SMAPI, no live Jellyfin. The suite parser must
return the full consideredIntents hypotheses through the ONE extractor
(smapi_client.considered_hypotheses at module level; smapi_ops re-exports
it on its existing import), not grow a second parser.

Placed here (not tests/integration/) because that directory's conftest has
an autouse jellyfin_client fixture every test must satisfy; these tests are
pure-parse and must run without any live service.
"""
import _paths
_paths.bootstrap()

import pytest
from smapi_client import MalformedResponseError, SmapiClient, SmapiError


def _fixture_response():
    """Live probe shape (2026-11-25): top-level selectedIntent +
    consideredIntents + sessionEnded; full Intent objects, no score."""
    return {
        "selectedIntent": {
            "name": "PlaySongIntent",
            "slots": {"song": {"name": "song", "value": "la canzone x"}},
        },
        "consideredIntents": [
            {"name": "PlayAlbumIntent",
             "slots": {"album": {"value": "x",
                                 "resolutions": {"resolutionsPerAuthority": [
                                     {"authority": "amzn1.er-authority..Album",
                                      "status": {"code": "ER_SUCCESS_MATCH"},
                                      "values": [{"value": {"name": "Album X",
                                                            "id": "A1"}}]}]}}}},
            {"name": "FallbackIntent"},
        ],
        "sessionEnded": False,
    }


def test_parse_success_returns_considered_hypotheses():
    result = SmapiClient.parse_profile_nlu_result(_fixture_response())
    assert result["intent"] == "PlaySongIntent"
    assert result["considered"] == [
        {"intent": "PlayAlbumIntent",
         "slots": {"album": "x"},
         "er": {"album": [{"authority": "amzn1.er-authority..Album",
                           "code": "ER_SUCCESS_MATCH", "canonical": "Album X"}]}},
        {"intent": "FallbackIntent", "slots": {}, "er": {}},
    ]


def test_parse_no_selection_raises_with_considered_names():
    response = {"selectedIntent": None,
                "consideredIntents": [{"name": "IntentForDifferentSkill"},
                                      {"name": "FallbackIntent"}]}
    with pytest.raises(SmapiError, match="No selectedIntent; considered"):
        SmapiClient.parse_profile_nlu_result(response)


def test_parse_no_selection_no_considered():
    with pytest.raises(SmapiError,
                       match="No selectedIntent or consideredIntents"):
        SmapiClient.parse_profile_nlu_result({"selectedIntent": None})


def test_parse_confident_selection_empty_considered():
    """Confident selections often carry an EMPTY consideredIntents; the
    considered key must still be present (empty list), and the empty case
    must not be confused with the no-selection error branch."""
    response = {"selectedIntent": {"name": "FindSongIntent", "slots": {}},
                "consideredIntents": []}
    result = SmapiClient.parse_profile_nlu_result(response)
    assert result["intent"] == "FindSongIntent"
    assert result["considered"] == []


# --- JF-936: null-shape hardening (one malformed response, one row) ---

def test_parse_null_selected_intent_slots_degrades_to_empty():
    """A selectedIntent with "slots": null keeps its intent with empty slots
    instead of crashing the caller's battery."""
    result = SmapiClient.parse_profile_nlu_result(
        {"selectedIntent": {"name": "PlaySongIntent", "slots": None}})
    assert result["intent"] == "PlaySongIntent"
    assert result["slots"] == {}


def test_parse_non_dict_selected_intent_is_malformed_error():
    """A truthy NON-dict selectedIntent raised AttributeError (aborting the
    lab battery); it now raises the DISTINCT MalformedResponseError (not a
    SmapiError: the battery runner overloads that as NO_SELECTION, and an
    unmarked degrade would read as an NLU-selection regression in triage)
    so the runner maps it to a marked ERROR row."""
    with pytest.raises(MalformedResponseError, match="not a dict: str"):
        SmapiClient.parse_profile_nlu_result(
            {"selectedIntent": "PlaySongIntent",
             "consideredIntents": [{"name": "FallbackIntent"}]})


def test_parse_nlu_result_null_slots_degrades_to_empty():
    """The simulate parser's None.items() pin: a considered intent whose
    slots key is null kept its name but raised AttributeError, which escaped
    the (KeyError, IndexError, TypeError) catch as a raw traceback."""
    response = {"result": {"alexaExecutionInfo": {
        "consideredIntents": [{"name": "PlaySongIntent", "slots": None}]}}}
    result = SmapiClient.parse_nlu_result(response)
    assert result["intent"] == "PlaySongIntent"
    assert result["slots"] == {}


def test_parse_nlu_result_non_dict_intent_converts_to_smapi_error():
    """AttributeError joined the unexpected-structure catch: a non-dict
    considered intent now refuses as SmapiError instead of tracing."""
    response = {"result": {"alexaExecutionInfo": {"consideredIntents": ["junk"]}}}
    with pytest.raises(SmapiError, match="Unexpected SMAPI response structure"):
        SmapiClient.parse_nlu_result(response)
