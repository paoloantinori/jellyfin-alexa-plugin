"""Name-level catalog snapshot producer (JF-954 subtask 1).

Reads item NAMES from a Jellyfin server and builds a snapshot with producer
``jellyfin-names`` under the ``"*"`` locale. This producer is NAME-LEVEL: it
does NOT reproduce the plugin's phonetic synonyms, the 140-character slot
value truncation or the 50000-value cap, so its output underestimates what
catalog sync actually uploads. The authoritative producer is the plugin
export (JF-954 subtask 8); the snapshot format documented in
docs/nlu-static-analysis.md is the contract between the two.

Authentication uses the query parameter ``ApiKey=<key>`` (capital A and K):
Jellyfin 12 rejects the ``X-Emby-Token`` header and the lowercase
``api_key`` query parameter with 401 on the general API surface. The API
key never appears in printed output or written files; error text is
redacted before it reaches a terminal.
"""

from __future__ import annotations

import json
import os
import re
import urllib.parse
import urllib.request
from typing import Callable

from nlu_static.effective_model import SNAPSHOT_GLOB_KEY, SNAPSHOT_SCHEMA

PRODUCER_NAME = "jellyfin-names"

# Jellyfin item type -> snapshot slot type name (the snapshot-format mapping).
ITEM_TYPE_TO_SLOT_TYPE: dict[str, str] = {
    "Series": "SeriesName",
    "MusicArtist": "JellyfinArtist",
    "MusicAlbum": "AlbumName",
    "Movie": "MovieTitle",
    "AudioBook": "AudiobookTitle",
    "Audio": "SongTitle",
}

FetchJson = Callable[[str], object]


def _http_get_json(url: str) -> object:
    """GET ``url`` and return the parsed JSON body.

    THE injection seam of this producer: tests and the plugin export replace
    this one function; nothing else here performs I/O. The url embeds the
    API key via the ApiKey query parameter, so callers must never log or
    print it.
    """
    request = urllib.request.Request(url, headers={"Accept": "application/json"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def build_snapshot(
    url: str,
    api_key: str,
    fetch_json: FetchJson | None = None,
    item_types: tuple[str, ...] | None = None,
) -> dict:
    """Build a snapshot dict from a Jellyfin server's item names.

    One GET per item type (``/Artists`` for MusicArtist, ``/Items`` with
    ``IncludeItemTypes`` and ``Recursive=true`` otherwise). Names are
    de-duplicated case-insensitively, the first spelling seen wins, and the
    kept names are sorted. Item types with no names are omitted. Slot types
    with zero values are deliberately left OUT of the snapshot: an empty
    replacement list would wipe the type on the live skill. Pass
    ``fetch_json`` to fake the HTTP layer; pass ``item_types`` to fetch a
    subset (default: every mapping in ITEM_TYPE_TO_SLOT_TYPE).
    """
    fetch = fetch_json if fetch_json is not None else _http_get_json
    types = item_types if item_types is not None else tuple(ITEM_TYPE_TO_SLOT_TYPE)
    table: dict[str, list[dict]] = {}
    for item_type in types:
        slot_type = ITEM_TYPE_TO_SLOT_TYPE.get(item_type)
        if slot_type is None:
            known = ", ".join(sorted(ITEM_TYPE_TO_SLOT_TYPE))
            raise ValueError(f"unknown Jellyfin item type {item_type!r}; known types: {known}")
        names = _item_names(url, api_key, item_type, fetch)
        if not names:
            continue
        table[slot_type] = [{"value": name, "synonyms": []} for name in sorted(names)]
    return {
        "schema": SNAPSHOT_SCHEMA,
        "producer": PRODUCER_NAME,
        "locales": {SNAPSHOT_GLOB_KEY: table},
    }


def resolve_connection(url: str | None, api_key: str | None) -> tuple[str, str]:
    """Resolve the Jellyfin connection from flags or environment.

    ``--url``/``--api-key`` win; otherwise ``JELLYFIN_URL`` and
    ``JELLYFIN_API_KEY`` from the environment. Raises ValueError naming the
    missing pieces when either is absent.
    """
    resolved_url = url or os.environ.get("JELLYFIN_URL", "")
    resolved_key = api_key or os.environ.get("JELLYFIN_API_KEY", "")
    missing = [
        name
        for value, name in ((resolved_url, "JELLYFIN_URL"), (resolved_key, "JELLYFIN_API_KEY"))
        if not value
    ]
    if missing:
        raise ValueError(
            "connection needs --url/--api-key or env JELLYFIN_URL/JELLYFIN_API_KEY; missing: "
            + ", ".join(missing)
        )
    return resolved_url, resolved_key


def redact(text: str, api_key: str) -> str:
    """Remove the API key and any ApiKey query assignment from error text."""
    if api_key:
        text = text.replace(api_key, "<redacted>")
    return re.sub(r"(?i)([?&])apikey=[^&\s]*", r"\1apikey=<redacted>", text)


def _item_names(url: str, api_key: str, item_type: str, fetch_json: FetchJson) -> list[str]:
    """Fetch the names of every item of one type, deduped case-insensitively.

    An unexpected payload shape raises ValueError: a wrong URL or a changed
    endpoint must fail loudly, not silently omit the type from the snapshot
    (the omit rule is reserved for a genuinely EMPTY library section).
    """
    base = url.rstrip("/")
    if item_type == "MusicArtist":
        path = "/Artists"
        query: dict[str, str] = {"ApiKey": api_key}
    else:
        path = "/Items"
        query = {
            "IncludeItemTypes": item_type,
            "Recursive": "true",
            "Fields": "",
            "ApiKey": api_key,
        }
    full_url = base + path + "?" + urllib.parse.urlencode(query)
    payload = fetch_json(full_url)
    if not isinstance(payload, dict) or "Items" not in payload:
        raise ValueError(
            f"{path} for {item_type} returned an unexpected payload "
            "(no 'Items' key); check --url and the Jellyfin version"
        )
    names: list[str] = []
    seen: set[str] = set()
    for item in payload["Items"]:
        name = item.get("Name") if isinstance(item, dict) else None
        if not isinstance(name, str) or not name.strip():
            continue
        key = name.casefold()
        if key in seen:
            continue
        seen.add(key)
        names.append(name)
    return names
