"""Simulator E2E tests hitting the deployed Jellyfin plugin directly.

Uses the built-in Simulator endpoint to test handler code without going
through Alexa's NLU. This verifies bug fixes and fuzzy matching behavior
against the actual server.

Requires a running Jellyfin server with the plugin deployed.
Configure via --jellyfin-url and --jellyfin-api-key (or env vars).
"""

from __future__ import annotations

import logging
import os

import pytest
import requests

logger = logging.getLogger("simulator.test")

SIMULATOR_PATH = "/Plugins/AlexaSkill/Simulator/Intent"

# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(scope="module")
def sim_url(jellyfin_url):
    """Full simulator endpoint URL."""
    assert jellyfin_url, "JELLYFIN_URL is required for simulator tests"
    return f"{jellyfin_url.rstrip('/')}{SIMULATOR_PATH}"


@pytest.fixture(scope="module")
def sim_headers(jellyfin_api_key):
    """HTTP headers with auth token."""
    assert jellyfin_api_key, "JELLYFIN_API_KEY is required for simulator tests"
    return {
        "X-Emby-Token": jellyfin_api_key,
        "Content-Type": "application/json",
    }


@pytest.fixture(scope="module")
def jellyfin_url(request):
    url = request.config.getoption("--jellyfin-url") or os.environ.get("JELLYFIN_URL", "")
    return url


@pytest.fixture(scope="module")
def jellyfin_api_key(request):
    key = request.config.getoption("--jellyfin-api-key") or os.environ.get("JELLYFIN_API_KEY", "")
    return key


def _simulate(sim_url: str, headers: dict, intent: str, slots: dict, locale: str = "it-IT") -> dict:
    """Fire a simulator request and return the parsed response."""
    payload = {
        "intentName": intent,
        "slots": slots,
        "locale": locale,
    }
    resp = requests.post(sim_url, headers=headers, json=payload, timeout=30)
    resp.raise_for_status()
    return resp.json()


def _has_directive(response: dict, directive_type: str) -> bool:
    """Check if the response contains a specific directive type."""
    directives = response.get("response", {}).get("directives", [])
    return any(d.get("type", "") == directive_type for d in directives)


def _get_speech_text(response: dict) -> str:
    """Extract plain text or SSML from the response speech."""
    speech = response.get("response", {}).get("outputSpeech", {})
    if not speech:
        return ""
    return speech.get("text", "") or speech.get("ssml", "")


# ---------------------------------------------------------------------------
# Test: PlayArtistSongsIntentHandler fuzzy prefix fallback
# ---------------------------------------------------------------------------


class TestPlayArtistSongsFuzzyFallback:
    """Verify that truncated/mispelled artist names still resolve via fuzzy prefix search.

    Before the fix, a truncated name like "soul coughin" (ASR truncation) would
    return "not found" because the SearchTerm query was exact. The fix adds a
    NameStartsWith prefix search followed by FuzzyMatch on the results.
    """

    @pytest.mark.simulator
    def test_truncated_artist_name_still_plays(self, sim_url, sim_headers):
        """'soul coughin' (truncated) should still find 'Soul Coughing' via fuzzy fallback."""
        result = _simulate(sim_url, sim_headers, "PlayArtistSongsIntent", {
            "musician": "soul coughin",
        })

        speech = _get_speech_text(result)
        has_play = _has_directive(result, "AudioPlayer.Play")

        # Should NOT be a "not found" response
        assert "non trovo" not in speech.lower() or has_play, (
            f"Expected fuzzy match to find 'Soul Coughing', got speech: {speech[:200]}"
        )
        assert has_play, (
            f"Expected AudioPlayer.Play directive for truncated artist name, "
            f"got directives: {[d.get('type') for d in result.get('response', {}).get('directives', [])]}"
        )

    @pytest.mark.simulator
    def test_exact_artist_name_still_works(self, sim_url, sim_headers):
        """Exact artist name should still work (no regression)."""
        result = _simulate(sim_url, sim_headers, "PlayArtistSongsIntent", {
            "musician": "Soul Coughing",
        })

        has_play = _has_directive(result, "AudioPlayer.Play")
        speech = _get_speech_text(result)

        assert has_play, (
            f"Expected AudioPlayer.Play for exact artist name, got speech: {speech[:200]}"
        )

    @pytest.mark.simulator
    def test_gibberish_artist_returns_not_found(self, sim_url, sim_headers):
        """Complete nonsense should still return 'not found'."""
        result = _simulate(sim_url, sim_headers, "PlayArtistSongsIntent", {
            "musician": "xyznonexistentartist123",
        })

        speech = _get_speech_text(result)
        # Should be a tell (not a play directive)
        has_play = _has_directive(result, "AudioPlayer.Play")
        assert not has_play, "Expected no playback for nonexistent artist"
        assert speech, "Expected a spoken response for not-found case"


# ---------------------------------------------------------------------------
# Test: PlaySongIntentHandler fuzzy prefix fallback
# ---------------------------------------------------------------------------


class TestPlaySongFuzzyFallback:
    """Same fuzzy prefix fallback, but for PlaySongIntentHandler.

    PlaySong uses the artist name as a filter when searching for a specific song.
    The fix applies the same NameStartsWith + FuzzyMatch strategy.
    """

    @pytest.mark.simulator
    def test_truncated_artist_with_song(self, sim_url, sim_headers):
        """Song request with truncated artist name should still resolve."""
        result = _simulate(sim_url, sim_headers, "PlaySongIntent", {
            "song": "Screenwriter's Blues",
            "musician": "soul coughin",
        })

        speech = _get_speech_text(result)
        has_play = _has_directive(result, "AudioPlayer.Play")

        # Should not be a "not found" response
        assert "non trovo" not in speech.lower() or has_play, (
            f"Expected fuzzy match, got speech: {speech[:200]}"
        )

    @pytest.mark.simulator
    def test_song_without_artist_returns_response(self, sim_url, sim_headers):
        """Song-only request (no artist) should return a valid response."""
        result = _simulate(sim_url, sim_headers, "PlaySongIntent", {
            "song": "Circles",
        })

        # Should get a valid response (play or ask for artist), not a crash
        response_body = result.get("response", {})
        has_speech = bool(response_body.get("outputSpeech"))
        has_directives = bool(response_body.get("directives"))
        assert has_speech or has_directives, "Expected a response for song-only request"


# ---------------------------------------------------------------------------
# Test: HandleFuzzyMiss null-safety
# ---------------------------------------------------------------------------


class TestHandleFuzzyMissNullSafety:
    """Verify the NullReferenceException fix in BaseHandler.HandleFuzzyMiss.

    The old code called `autoPlayFunc(best)` and then accessed
    `playResponse.Response` without a null check. When autoPlayFunc returned
    null (used as a side-effect callback), it crashed.

    The fix: if playResponse is null, return SuggestionHandled early.
    """

    @pytest.mark.simulator
    def test_multiple_artist_matches_no_crash(self, sim_url, sim_headers):
        """A musician name matching multiple artists should not crash.

        This exercises the HandleFuzzyMiss path where autoPlayFunc returns null.
        The handler uses the callback to narrow the artist list — it returns null
        because it doesn't produce a SkillResponse itself. Before the fix, this
        caused a NullReferenceException.
        """
        # Use a very generic name likely to match multiple artists
        # The key is that the code path through HandleFuzzyMiss doesn't crash
        result = _simulate(sim_url, sim_headers, "PlayArtistSongsIntent", {
            "musician": "various artists",
        })

        # We don't assert success — just that it returns a valid response
        # (no 500 error from NullReferenceException)
        assert result, "Expected a valid response, not a crash"
        response_body = result.get("response", {})
        assert response_body is not None, "Response body should exist (no crash)"
        # Either we get speech or directives — both are valid
        has_speech = bool(response_body.get("outputSpeech"))
        has_directives = bool(response_body.get("directives"))
        should_end = response_body.get("shouldEndSession")
        assert has_speech or has_directives or should_end is not None, (
            "Expected some response content (speech, directives, or session end)"
        )


# ---------------------------------------------------------------------------
# Test: Basic smoke — other intents still work after deploy
# ---------------------------------------------------------------------------


class TestSmokeAfterDeploy:
    """Quick smoke tests confirming the deployed plugin handles basic intents."""

    @pytest.mark.simulator
    def test_play_favorites(self, sim_url, sim_headers):
        result = _simulate(sim_url, sim_headers, "PlayFavoritesIntent", {
            "media_type": "musica",
        })
        has_play = _has_directive(result, "AudioPlayer.Play")
        assert has_play, "Expected playback for PlayFavoritesIntent"

    @pytest.mark.simulator
    def test_search_media(self, sim_url, sim_headers):
        result = _simulate(sim_url, sim_headers, "SearchMediaIntent", {
            "query": "Soul Coughing",
        })
        # Search should return something (speech with results or a play directive)
        speech = _get_speech_text(result)
        has_play = _has_directive(result, "AudioPlayer.Play")
        assert speech or has_play, "Expected search results or playback"
