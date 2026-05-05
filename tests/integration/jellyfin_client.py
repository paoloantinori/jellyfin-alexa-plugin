"""Jellyfin REST API client for E2E test side-effect verification."""

from __future__ import annotations

import logging
from typing import Any

import requests

logger = logging.getLogger("e2e.jellyfin")


class JellyfinError(Exception):
    """Raised when a Jellyfin API call fails."""


class JellyfinClient:
    """Lightweight client for the Jellyfin REST API.

    Used by E2E tests to verify side effects (active sessions, playback
    state, favorites) after skill invocations.
    """

    def __init__(self, base_url: str, api_key: str, user_id: str,
                 timeout: float = 10.0):
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.user_id = user_id
        self.timeout = timeout

    @property
    def _headers(self) -> dict[str, str]:
        return {
            "X-Emby-Token": self.api_key,
            "Content-Type": "application/json",
        }

    def _get(self, path: str, params: dict[str, Any] | None = None) -> Any:
        url = f"{self.base_url}{path}"
        resp = requests.get(
            url, headers=self._headers, params=params,
            timeout=self.timeout,
        )
        if resp.status_code >= 400:
            raise JellyfinError(
                f"GET {path} returned {resp.status_code}: {resp.text[:300]}"
            )
        return resp.json()

    def _post(self, path: str, json_body: Any = None) -> Any:
        url = f"{self.base_url}{path}"
        resp = requests.post(
            url, headers=self._headers, json=json_body,
            timeout=self.timeout,
        )
        if resp.status_code >= 400:
            raise JellyfinError(
                f"POST {path} returned {resp.status_code}: {resp.text[:300]}"
            )
        return resp.json() if resp.text.strip() else {}

    def _delete(self, path: str) -> None:
        url = f"{self.base_url}{path}"
        resp = requests.delete(url, headers=self._headers, timeout=self.timeout)
        if resp.status_code >= 400:
            raise JellyfinError(
                f"DELETE {path} returned {resp.status_code}: {resp.text[:300]}"
            )

    def health_check(self) -> bool:
        """Return True if the Jellyfin server is reachable and healthy."""
        try:
            resp = requests.get(
                f"{self.base_url}/System/Info/Public",
                timeout=self.timeout,
            )
            return resp.status_code == 200
        except requests.RequestException:
            return False

    def get_sessions(self) -> list[dict[str, Any]]:
        """Get active sessions (includes NowPlayingQueue and playback state)."""
        return self._get("/Sessions")

    def get_user_sessions(self) -> list[dict[str, Any]]:
        """Get sessions for the configured user only."""
        sessions = self.get_sessions()
        return [
            s for s in sessions
            if s.get("UserId") == self.user_id
            or s.get("UserName", "").lower() == self.user_id.lower()
        ]

    def is_playing(self) -> bool:
        """Check if the configured user currently has media playing."""
        for session in self.get_user_sessions():
            if session.get("NowPlayingItem"):
                return True
        return False

    def get_now_playing(self) -> dict[str, Any] | None:
        """Return the NowPlayingItem for the user, or None."""
        for session in self.get_user_sessions():
            item = session.get("NowPlayingItem")
            if item:
                return item
        return None

    def stop_playback(self) -> None:
        """Stop playback on all sessions for the configured user."""
        for session in self.get_user_sessions():
            session_id = session.get("Id")
            if session_id:
                try:
                    self._post(
                        f"/Sessions/{session_id}/Command",
                        json_body={"Name": "Stop"},
                    )
                except JellyfinError:
                    logger.debug(
                        "Could not stop session %s (may already be idle)",
                        session_id[:8],
                    )

    def get_favorites(self) -> list[dict[str, Any]]:
        """Get the user's favorite items."""
        data = self._get(
            f"/Users/{self.user_id}/Items",
            params={"IsFavorite": "true", "Recursive": "true"},
        )
        return data.get("Items", [])

    def is_favorite(self, item_id: str) -> bool:
        """Check if a specific item is marked as favorite."""
        try:
            data = self._get(f"/Users/{self.user_id}/Items/{item_id}")
            return data.get("UserData", {}).get("IsFavorite", False)
        except JellyfinError:
            return False

    def unfavorite(self, item_id: str) -> None:
        """Remove an item from favorites (cleanup helper)."""
        try:
            self._delete(f"/Users/{self.user_id}/FavoriteItems/{item_id}")
        except JellyfinError:
            logger.debug("Could not unfavorite %s for cleanup", item_id[:8])

    def search_items(self, search_term: str,
                     item_types: list[str] | None = None,
                     limit: int = 5) -> list[dict[str, Any]]:
        """Search the library for items matching a term."""
        params: dict[str, Any] = {
            "SearchTerm": search_term,
            "Recursive": "true",
            "Limit": limit,
        }
        if item_types:
            params["IncludeItemTypes"] = ",".join(item_types)
        data = self._get(f"/Users/{self.user_id}/Items", params=params)
        return data.get("Items", [])
