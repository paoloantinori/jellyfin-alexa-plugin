"""Catalog snapshot producers (JF-954 subtasks 1 and 8).

``build_snapshot`` reads item NAMES from a Jellyfin server and builds a
snapshot with producer ``jellyfin-names`` under the ``"*"`` locale. That
producer is NAME-LEVEL: it does NOT reproduce the plugin's phonetic
synonyms, the 140-character slot value truncation or the 50000-value cap,
so its output underestimates what catalog sync actually uploads.

``build_snapshot_from_plugin`` is the AUTHORITATIVE producer (JF-954
subtask 8): it fetches the snapshot the PLUGIN export endpoint builds with
the same code the sync uploads (producer ``plugin``, per locale), so
synonyms, seed merges, truncation and the value cap are reproduced exactly.

The snapshot format documented in docs/nlu-static-analysis.md is the
contract between the two.

Authentication rides the ``Authorization: MediaBrowser Token="<key>"``
header (verified working on Jellyfin 12 for /Items and /Artists), keeping
the key out of URLs that server access logs or error objects might record.
Jellyfin 12 also rejects the ``X-Emby-Token`` header and the lowercase
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

from nlu_static.effective_model import SNAPSHOT_GLOB_KEY, SNAPSHOT_SCHEMA, validate_snapshot

PRODUCER_NAME = "jellyfin-names"
PLUGIN_PRODUCER_NAME = "plugin"
PLUGIN_SNAPSHOT_PATH = "/Plugins/AlexaSkill/Diagnostics/CatalogSnapshot"

# Jellyfin item type -> snapshot slot type name (the snapshot-format mapping).
ITEM_TYPE_TO_SLOT_TYPE: dict[str, str] = {
    "Series": "SeriesName",
    "MusicArtist": "JellyfinArtist",
    "MusicAlbum": "AlbumName",
    "Movie": "MovieTitle",
    "AudioBook": "AudiobookTitle",
    "Audio": "SongTitle",
}

FetchJson = Callable[[str, str], object]


def _http_get_json(url: str, api_key: str) -> object:
    """GET ``url`` with the MediaBrowser token header, return the parsed body.

    THE injection seam of this producer: tests and the plugin export replace
    this one function; nothing else here performs I/O. The API key travels
    in the Authorization header, never in the URL, so urls stay safe to log;
    the key itself must still never be printed.
    """
    request = urllib.request.Request(url, headers={
        "Accept": "application/json",
        "Authorization": f'MediaBrowser Token="{api_key}"',
    })
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


def build_snapshot_from_plugin(
    base_url: str,
    api_key: str,
    user_id: str,
    locale: str | None = None,
    fetch_json: FetchJson | None = None,
) -> dict:
    """Fetch the AUTHORITATIVE snapshot from the plugin export endpoint.

    This is JF-954 subtask 8's producer: the PLUGIN builds every payload with
    the same code the catalog sync uploads, so the snapshot reproduces the
    plugin's phonetic synonyms, the seed merges, the 140-character value
    truncation, the de-duplication and the 50000-value cap exactly, per-user
    library-filtered. The endpoint is
    ``GET /Plugins/AlexaSkill/Diagnostics/CatalogSnapshot?userId=<guid>
    [&locale=<locale>]`` and it is admin-gated (RequiresElevation) on the
    plugin side; it performs no SMAPI call of its own.

    The request rides the SAME injectable seam and the SAME
    ``Authorization: MediaBrowser Token="<key>"`` header form as the
    name-level producer, so the key never lands in a URL. The response must
    carry producer ``plugin`` and must pass ``validate_snapshot``; anything
    else raises ValueError, so a wrong endpoint or a name-level document can
    never pass as the authoritative snapshot. Pass ``fetch_json`` to fake the
    HTTP layer in tests.
    """
    fetch = fetch_json if fetch_json is not None else _http_get_json
    query: dict[str, str] = {"userId": user_id}
    if locale:
        query["locale"] = locale
    url = base_url.rstrip("/") + PLUGIN_SNAPSHOT_PATH + "?" + urllib.parse.urlencode(query)
    payload = fetch(url, api_key)
    if not isinstance(payload, dict):
        raise ValueError(
            "the plugin export returned a non-object body; check --url and the plugin version"
        )
    producer = payload.get("producer")
    if producer != PLUGIN_PRODUCER_NAME:
        raise ValueError(
            f"the plugin export must carry producer {PLUGIN_PRODUCER_NAME!r}, got {producer!r}"
        )
    locales = payload.get("locales")
    if isinstance(locales, dict) and not locales:
        # The export is honest about nothing-to-upload (the C# side emits an
        # empty table when the sync would process no locale or no type for
        # this user), but the snapshot FORMAT refuses an empty table, so say
        # exactly that instead of the generic schema error.
        raise ValueError(
            "the plugin export carries no locale entries: the sync would "
            "upload nothing for this user (an empty library under the "
            "CatalogSyncLocales roster, or a --locale the sync would not "
            "process, ar-SA included)"
        )
    validate_snapshot(payload, context="plugin export")
    return payload


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

    Authentication rides the ``Authorization: MediaBrowser Token="..."``
    header (verified working on Jellyfin 12 for /Items and /Artists), so
    the API key never lands in a URL that server access logs or error
    objects might record; the ApiKey query parameter works too but is
    deliberately not used for that reason. An unexpected payload shape
    raises ValueError: a wrong URL or a changed endpoint must fail loudly,
    not silently omit the type from the snapshot (the omit rule is
    reserved for a genuinely EMPTY library section).
    """
    base = url.rstrip("/")
    if item_type == "MusicArtist":
        path = "/Artists"
        query: dict[str, str] = {}
    else:
        path = "/Items"
        query = {
            "IncludeItemTypes": item_type,
            "Recursive": "true",
            "Fields": "",
        }
    full_url = base + path + "?" + urllib.parse.urlencode(query)
    payload = fetch_json(full_url, api_key)
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
