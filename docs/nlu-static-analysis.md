# NLU static analysis toolkit

`scripts/nlu_static/` is the deterministic triage toolkit for the interaction models (JF-954). It reads the committed `model_<locale>.json` files, the NLU fixtures and (optionally) a catalog snapshot, and answers build-time questions about vocabulary, token ownership and sample shape that used to cost hours of live probing. One CLI entry point serves every tool: `python3 scripts/nlu_static/cli.py <subcommand>`, documented below per subcommand. Later JF-954 subtasks add their own subcommands to the same CLI; this page grows with them.

These tools detect NECESSARY conditions for a break and classify failures with reproducible rules. They do NOT predict how Amazon's trainer routes an utterance: a clean static report never certifies live routing, and a static finding about routing still needs profile-nlu, simulate-skill or a device test to confirm behavior. Treat every output here as one input to triage, not as the verdict.

Run everything from the repository root. Output is deterministic by contract: sorted keys, explicitly sorted lists, no timestamps, no absolute paths, no dependence on dict iteration order. Output goes to stdout by default; pass `--out <file>` to write a file. A locale a tool cannot handle is reported with an explicit `SKIP <locale>: <reason>` line (today that is only ja-JP, whose samples carry no word spaces); silent skips are forbidden.

## The effective model

The committed model ships a small static seed for every catalog-backed slot type: es-ES `SeriesName`, for example, carries ten seed titles such as "Breaking Bad". On a user's live skill, catalog sync replaces that seed with the user's own library values, so the committed vocabulary is not the vocabulary the skill actually trains against. The EFFECTIVE model closes that gap: it is the committed model with the values of the catalog-backed types (AlbumName, AudiobookTitle, JellyfinArtist, MovieTitle, SeriesName, SongTitle; the source of truth is `CatalogSlotTypeNames` in `Alexa/Catalog/CatalogSlotTypes.cs`) replaced by the values of a catalog snapshot. Without a snapshot the effective model is the committed model verbatim, with every type labeled STATIC or CATALOG, which is already enough to tell a seed-only vocabulary from a replaced one.

The `effective` subcommand prints a deterministic JSON summary: the model source (`committed` or `snapshot`), any notes about snapshot types the locale does not declare, and per slot type its kind, entry count and value source.

```
python3 scripts/nlu_static/cli.py effective --locale es-ES
python3 scripts/nlu_static/cli.py effective --locale es-ES --snapshot catalog-snapshot.json
```

## Catalog snapshot format

A snapshot is a JSON file of schema 1: a producer name and a `locales` object keyed by locale, mapping slot type names to lists of entries with a string `value` and optional `synonyms`. The locale key `"*"` applies to every locale that has no entry of its own, and a per-locale entry overrides the `"*"` entry for that locale.

```json
{
  "schema": 1,
  "producer": "jellyfin-names",
  "locales": {
    "*": {
      "SeriesName": [ {"value": "Adolescencia", "synonyms": []} ],
      "JellyfinArtist": [ {"value": "Nightwish", "synonyms": []} ]
    }
  }
}
```

`load_snapshot` validates the file before any tool uses it: an unknown or missing schema number, a non-list type entry, and an entry without a string `value` are each rejected with a `ValueError` naming the offending path. Values are stored exactly as given; normalization happens at comparison time inside the tools.

### The `snapshot` subcommand and the name-level caveat

The `snapshot` subcommand reads item names from a Jellyfin server and writes this format with producer `jellyfin-names` under the `"*"` locale: Series to SeriesName, MusicArtist to JellyfinArtist, MusicAlbum to AlbumName, Movie to MovieTitle, AudioBook to AudiobookTitle, Audio to SongTitle. Connection comes from `--url`/`--api-key` or the environment variables `JELLYFIN_URL` and `JELLYFIN_API_KEY`. Authentication rides the `Authorization: MediaBrowser Token="<key>"` header (verified working on Jellyfin 12 for /Items and /Artists), keeping the key out of URLs that server access logs or error objects might record; Jellyfin 12 also rejects the `X-Emby-Token` header and the lowercase `api_key` query with 401. The key is never printed or written, including in error messages. Item types with no names are omitted so a snapshot can never replace a type with an empty list.

```
python3 scripts/nlu_static/cli.py snapshot --url http://jellyfin.local:8096 --api-key <key> --out catalog-snapshot.json
JELLYFIN_URL=http://jellyfin.local:8096 JELLYFIN_API_KEY=<key> python3 scripts/nlu_static/cli.py snapshot --types Series MusicArtist
```

CAVEAT: this producer is NAME-LEVEL. It does NOT reproduce the plugin's phonetic synonyms, does NOT apply the 140-character slot value truncation, and does NOT enforce the 50000-value cap, so its output understates what catalog sync actually uploads to Amazon. It can also OVERSTATE the live vocabulary in the other direction on a multi-user server: it queries admin-wide, while the plugin's authoritative sync is per-user library-filtered (LibraryFilter.ApplyLibraryFilter), SortName-ordered and capped, so names from libraries the synced user cannot access appear here but never reach Amazon. Use it for local static analysis where the raw names are what matter; the authoritative producer is the plugin export (JF-954 subtask 8, which adds `--from-plugin` to this same subcommand), and the format above is the contract between the two.
