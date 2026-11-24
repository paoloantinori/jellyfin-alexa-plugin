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

## The `tokens` subcommand: token-ownership collision lint

Every intent sample has a CARRIER: the token list left after removing the `{slot}` placeholders. Every custom slot type owns ENTRIES: each of its values and synonyms, tokenized the same way. A collision exists when a token the trainer learns as a LITERAL of one intent's carrier is at the same time a vocabulary VALUE of a custom type another intent uses, because for titles Amazon does not know the trainer tends to prefer the intent whose type owns the word. The `tokens` subcommand finds three kinds of such ownership conflicts; the identity line printed for each finding is `locale|kind|type-or-types|entry|intent-or-empty` and is the stable key for grep, baselines and bug reports.

- `CARRIER_VS_VALUE` (the issue #46 class): an entry of custom type T appears as a contiguous token sequence inside the carrier of at least one sample of intent I, where I does not use T and at least one other intent J uses T. One finding per locale, T, entry and I; the detail line names the intents using T, how many samples of I contain the entry and the alphabetically first such sample as the example.
- `VALUE_IN_TWO_TYPES`: the same normalized entry text is owned by two different custom types of one locale. One finding per sorted type pair. The comparison is the shared normalizer's NFC composition plus casefold: "Jazz" in one type and "jazz" in another collide, and a decomposed spelling ("cafe" + combining accent) collides with its precomposed spelling ("café"), but an accented spelling never collides with an unaccented one ("café" and "cafe" stay distinct).
- `CATALOG_VS_MODEL` (only with `--snapshot`, never baselined): a snapshot entry of a catalog-backed type whose FULL normalized text equals a complete slot-free sample of any intent, or an entry of a STATIC custom type. This is the "a library song titled Stop or Jazz" shape: the catalog pushes a word that is also a trained literal, so an utterance made of that word alone can flip intent. Output per locale is the exact count per sub-kind plus the first 50 findings in sorted order, with a `... N more not shown` line when truncated.

Matching is index-driven (one inverted index per locale, token to sample positions; multi-token entries are matched by first-token lookup plus following-position verification), so the run cost scales with the model, not with entries times samples. Locales without word spaces get the explicit `SKIP ja-JP: no whitespace tokenization` line, which scopes to the TOKEN-dependent kinds (CARRIER_VS_VALUE and VALUE_IN_TWO_TYPES); CATALOG_VS_MODEL compares full normalized texts and needs no word tokens, so it still runs for ja-JP. Silent skips are forbidden.

```
python3 scripts/nlu_static/cli.py tokens --locale de-DE
python3 scripts/nlu_static/cli.py tokens
python3 scripts/nlu_static/cli.py tokens --locale de-DE --all
python3 scripts/nlu_static/cli.py tokens --snapshot catalog-snapshot.json
python3 scripts/nlu_static/cli.py tokens --update-baseline
```

### Reading a finding, and the issue #46 worked example

Issue #46 (de-DE): "spiele das Lied die streithörnchen" routed to MediaInfoIntent instead of PlaySongIntent. Root cause, found only after days of live probing: the word "Lied" was a synonym inside the custom type MediaInfoType (used by MediaInfoIntent for "what is playing" questions) and at the same time a literal carrier word of PlaySongIntent samples like "Spiele Lied {song}". Re-adding those two synonyms to today's committed de-DE model and running the lint reproduces the finding exactly:

```
de-DE|CARRIER_VS_VALUE|MediaInfoType|lied|PlaySongIntent
    using intents: MediaInfoIntent; samples: 2; example: "Spiele Lied {song}"
```

Read it as: in de-DE, the MediaInfoType entry "lied" (the kind is on the identity line) appears in the carrier of 2 PlaySongIntent samples, and PlaySongIntent does not use MediaInfoType while MediaInfoIntent does. That is the exact shape that lost the routing fight. JF-933 removed the song-noun synonyms from MediaInfoType in 16 locales, which is the correct FIX direction: delete the colliding synonym or qualify the carrier with a media noun, so the word belongs to one owner again. A finding is evidence of a necessary condition, not proof of a break: "album" in a MediaType value and in "play the album {album}" is a collision by this definition and is deliberate design, because the carrier is qualified enough that the fight does not arise. That is what the baseline is for.

### The baseline workflow

`scripts/nlu_static/token_collision_baseline.json` is a sorted JSON list of identity strings, one per finding the models legitimately carry. By default the CLI and the validator's Phase 11 print only findings NOT in the baseline; `--all` prints everything and annotates each line with `baselined: true` or `false`; the CLI also lists STALE baseline identities (entries the current models no longer produce, scoped to the locales this run linted, so a single-locale run never calls the other locales' entries stale). `--update-baseline` rewrites the baseline from the current committed models and prints how many identities were added and removed; CATALOG_VS_MODEL findings never enter it, because the catalog depends on one user's library and would freeze library names into the repo.

- FIX the model when the finding names a real contested word: a media noun or carrier word doubling as a type entry (the issue #46 shape), or an entry duplicated across two types where the duplication is not deliberate. The fix lives in the locale YAML templates and flows through regeneration; never hand-edit `model_*.json`.
- BASELINE the finding when it is legitimate design: qualified carriers ("play the album {album}" with a MediaType value "album"), shared vocabulary words no utterance actually contests. Baseline only what you have looked at; run `tokens --locale <locale> --all`, judge each line, then update. `--update-baseline` is a SCOPED merge: it rewrites the entries of the locales this run linted and keeps every other locale's entries verbatim, so judging one locale can never delete the others' known findings. It refuses `--snapshot`: the baseline tracks the committed models only, and letting snapshot (library) entries in would freeze one user's library names into the repo.
- After every model change run the CLI once before committing: a NEW non-baselined finding shows up in CI as a validator warning, and the wrong response to that warning is a blind `--update-baseline` that swallows a real collision.

## The `fixtures` subcommand: fixture values versus slot type vocabulary

Every NLU fixture row names an intent it expects (`expected_intent`, possibly `A|B` where either delivery counts) and, per slot, either an ANY expectation (`{}`: some non-empty value) or a PIN (`{value: "x"}`: that exact value; a bare YAML string is tolerated as a shorthand for the same pin). The `fixtures` subcommand asks the dictionary question behind every red row of this class: does the effective slot type the expected intent declares actually own that value (or, for ANY rows, at least one vocabulary entry that occurs in the utterance)? The comparison uses the shared normalizer, so case, NFC accent composition and surrounding whitespace fold away; ANY rows match vocabulary entries as contiguous token sequences, and ja-JP (no word spaces) matches as substring containment on the normalized text instead. No locale is skipped for its script: both matching rules work without word spaces, ja-JP included, so the ja-JP SKIP line of the token tools never fires here. A locale whose fixture file is missing or unparseable still reports the explicit `SKIP <locale>: ...` line (on stderr, so the JSON stays parseable). Slot types that are not closed custom types (AMAZON.SearchQuery, AMAZON.NUMBER, AMAZON.DURATION and the other builtin families) have no vocabulary to check, so their results are `NOT_APPLICABLE` and are never findings; a slot type the pinned classification table does not know at all is reported as an explicit `UNKNOWN_BUILTIN` note, because such a name must be added to the table rather than silently ignored.

Six statuses, all carried per result together with the slot's resolved type, its kind (`STATIC` or `CATALOG`) and the mode (`committed` or `snapshot`):

- `PRESENT`: the pinned value equals the canonical value or a synonym of the effective type, or an ANY row found a vocabulary entry as a contiguous token sequence in the utterance.
- `ABSENT`: a pinned value the effective type does not own. On a STATIC type this is a fixture bug (anti-pattern #8 of the project CLAUDE.md) or a stale model; on a CATALOG type in committed mode it is usually expected (see below).
- `NO_ENTRY_IN_UTTERANCE`: an ANY row whose utterance contains no entry of the effective type, so the row relies on the trainer generalizing beyond the vocabulary.
- `FIXTURE_SLOT_UNKNOWN`: none of the expected intent alternatives declares the slot. With `A|B` where only B declares the slot, nothing is reported against A; the check resolves the slot from the declaring alternative.
- `NOT_APPLICABLE`: the slot type is not a closed custom type.
- `ABSENT_IN_LIBRARY_SEED_ONLY`: snapshot mode only. The pinned value is missing from a NAME-LEVEL snapshot (producer `jellyfin-names`) but present in the committed seed of the replaced CATALOG type. Such a value is probably live anyway, because the plugin merges committed seeds into the real catalog payload for several types, so it is deliberately not plain `ABSENT`; under the authoritative producer (`plugin`, the JF-954.8 export) the payload already holds whatever the plugin merges, so the same case is plain `ABSENT`.

```
python3 scripts/nlu_static/cli.py fixtures --locale es-ES
python3 scripts/nlu_static/cli.py fixtures --locale es-ES --snapshot catalog-snapshot.json
python3 scripts/nlu_static/cli.py fixtures --format text
```

The JSON output (the default) is a sorted list of result objects with exactly the fields `locale`, `utterance`, `expected_intent`, `slot`, `type`, `type_kind`, `status`, `mode`, byte-identical across runs over the same inputs; the triage subtask consumes it. The text form prints the same findings, one heading line and one utterance line per finding, preceded by a per-status summary count. Diagnostics such as `SKIP fr-FR: no fixture file` and the `UNKNOWN_BUILTIN` notes go to stderr so the JSON stays parseable.

### The lab-green/live-red worked example (Breaking Bad)

On 2026-11-23 the live NLU suite reported the es/fr episode rows red, for example es-ES "reproduce breaking bad temporada uno episodio tres" expecting PlayEpisodeIntent with `series_name` = "breaking bad". The same rows were green on the lab skill. The cause was a vocabulary difference no live probing needed to find: the `series_name` slot uses the catalog-backed custom type SeriesName, whose committed model seed contains "Breaking Bad" (that is what the lab trains on), while the live skill's catalog sync replaces the seed with the user's library series and the maintainer's library has no Breaking Bad. A value that matches nothing in a catalog-backed type discourages selection of that intent (project finding JF-684), so an already marginal phrasing fell to another intent. Running the check with a snapshot whose SeriesName holds only library values reproduces the break without a single Amazon call: the committed mode reports the row `PRESENT` (the seed owns the pin), the snapshot mode reports it `ABSENT_IN_LIBRARY_SEED_ONLY` under the name-level producer, and an ANY row over the same utterance flips from `PRESENT` to `NO_ENTRY_IN_UTTERANCE` the moment the series leaves the snapshot. That is the exact lab-green/live-red delta the two-hour suite had to discover.

### Reading the findings, and the fixture-author rule

The validator runs this check as "Phase 12: fixture slot values versus slot type vocabulary (JF-954 warning check)" in committed mode, with the JF-954.3 partition: committed-mode rows on CATALOG types (both `ABSENT` pins and `NO_ENTRY_IN_UTTERANCE` ANY rows) are EXPECTED there, because fixtures deliberately pin values that exist in the test library but not in the committed seed, such as "adolescencia"; they are counted in one summary line instead of warning per row, and the full per-row list stays available through `fixtures --format json`. The individual warnings are the ones actionable against the committed tree: `ABSENT` on a STATIC type, `NO_ENTRY_IN_UTTERANCE` on a STATIC type (both mean the trainer has nothing to anchor the slot on), and `FIXTURE_SLOT_UNKNOWN` (a fixture naming a slot no expected intent declares).

Practical rule for fixture authors: a live fixture that pins a value on a catalog-backed slot (SeriesName, AlbumName, JellyfinArtist, MovieTitle, AudiobookTitle, SongTitle) should pin an item that exists in the test library, or the row's comment should mark it as catalog-dependent so a red row reads as "the live catalog lacks this item" instead of "the model is broken". A pinned value on a STATIC type must exist in the committed type (or the type must be extended through the template and regeneration); an ANY row should carry at least one vocabulary entry inside the utterance unless generalization is deliberately being tested. As everywhere in this toolkit, a finding is a necessary condition, not a routing verdict: `ABSENT` says the trainer has nothing to anchor on, not that Amazon will certainly misroute the row.

