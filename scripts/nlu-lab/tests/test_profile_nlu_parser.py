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
from smapi_client import SmapiClient, SmapiError


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
