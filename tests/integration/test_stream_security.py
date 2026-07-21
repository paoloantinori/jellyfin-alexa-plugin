"""E2E security tests for the signed item-scoped stream token (JF-309).

Tests that the video-audio streaming endpoints reject requests without a valid
token — proving the bare-GUID streaming bypass is closed on the live server.

The positive case (valid token → 200) requires the server-side HMAC secret and
is covered by unit tests (StreamTokenHelperTests) + on-device verification. These
e2e tests focus on the rejection paths that prove the security gate is live.

Run via run_e2e_tests.sh (same Jellyfin options):
    ./scripts/run_e2e_tests.sh --jellyfin-url URL --jellyfin-api-key KEY --jellyfin-user USER -v
"""

from __future__ import annotations

import pytest
import requests

pytestmark = pytest.mark.e2e


def _video_audio_url(jellyfin_url: str, item_id: str, suffix: str = "") -> str:
    """Build a video-audio endpoint URL for a given item."""
    base = jellyfin_url.rstrip("/")
    return f"{base}/alexaskill/api/video-audio/{item_id}{suffix}"


def test_stream_video_audio_no_token_returns_401(jellyfin_client):
    """A bare-GUID request to the MP4 endpoint with no token must be rejected."""
    item_id = jellyfin_client.get_first_audio_item_id()
    url = _video_audio_url(jellyfin_client.base_url, item_id)
    resp = requests.get(url, timeout=10, allow_redirects=False)
    assert resp.status_code == 401, f"Expected 401 for bare-GUID request, got {resp.status_code}"


def test_stream_hls_video_audio_no_token_returns_401(jellyfin_client):
    """A bare-GUID request to the HLS playlist endpoint with no token must be rejected."""
    item_id = jellyfin_client.get_first_audio_item_id()
    url = _video_audio_url(jellyfin_client.base_url, item_id, "/stream.m3u8")
    resp = requests.get(url, timeout=10, allow_redirects=False)
    assert resp.status_code == 401, f"Expected 401 for bare-GUID HLS request, got {resp.status_code}"


def test_get_segment_no_token_returns_401(jellyfin_client):
    """A bare-GUID segment request with no token must be rejected."""
    item_id = jellyfin_client.get_first_audio_item_id()
    url = _video_audio_url(jellyfin_client.base_url, item_id, "/segments/seg_0000.ts")
    resp = requests.get(url, timeout=10, allow_redirects=False)
    assert resp.status_code == 401, f"Expected 401 for bare-GUID segment request, got {resp.status_code}"


def test_stream_video_audio_garbage_token_returns_401(jellyfin_client):
    """A request with a malformed/garbage token must be rejected."""
    item_id = jellyfin_client.get_first_audio_item_id()
    url = _video_audio_url(jellyfin_client.base_url, item_id) + "?token=garbage"
    resp = requests.get(url, timeout=10, allow_redirects=False)
    assert resp.status_code == 401, f"Expected 401 for garbage token, got {resp.status_code}"


def test_stream_video_audio_wrong_item_token_returns_401(jellyfin_client):
    """A valid-format token for a different item must be rejected (HMAC item-binding)."""
    import hmac
    import hashlib
    import base64
    import time

    # Mint a token for item B, then request item A.
    item_a = jellyfin_client.get_first_audio_item_id()
    item_b = jellyfin_client.get_first_audio_item_id(exclude=item_a)

    # Read the server secret from the plugin config (the test has the Jellyfin API key).
    config_url = f"{jellyfin_client.base_url}/Plugins/c5df7de087774b3ca70d5c3dae359c9e/Configuration"
    config_resp = requests.get(config_url, headers={"X-Emby-Token": jellyfin_client.api_key}, timeout=10)
    secret = config_resp.json().get("StreamTokenSecret", "")
    if not secret:
        pytest.skip("StreamTokenSecret not configured on the live server")

    expires = int(time.time()) + 3600  # 1h in the future
    payload = f"{item_b}|{expires}".encode("ascii")
    sig = hmac.new(secret.encode("ascii"), payload, hashlib.sha256).digest()
    sig_b64 = base64.b64encode(sig).decode("ascii").rstrip("=").replace("+", "-").replace("/", "_")
    token = f"{expires}.{sig_b64}"

    url = _video_audio_url(jellyfin_client.base_url, item_a) + f"?token={token}"
    resp = requests.get(url, timeout=10, allow_redirects=False)
    assert resp.status_code == 401, f"Expected 401 for wrong-item token, got {resp.status_code}"
