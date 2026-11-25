# NLU static analysis toolkit

## Triage in five minutes

After a live suite reports red rows, the `triage` subcommand turns each red row of the recorded ledger session into one verdict (INFRA, UNSTABLE_BUILD, FIXTURE_VOCAB, KNOWN_FLAKY, CONTESTED, TOKEN_COLLISION, SYSTEMATIC_CANDIDATE, in that precedence), groups the rows into families and prints a report you can act on in minutes. The exact sequence after a red suite:

1. Take a catalog snapshot so the vocabulary rule answers the LIVE question, not the committed-seed one: `python3 scripts/nlu_static/cli.py snapshot --from-plugin --user-id <guid> --out catalog-snapshot.json` (the authoritative producer; without `--from-plugin` the name-level item-names producer runs).
2. Run triage against the latest complete session with that snapshot: `python3 scripts/nlu_static/cli.py triage --snapshot catalog-snapshot.json`. Without `--dir` it reads `tests/integration/results/` (or `NLU_LEDGER_DIR`); `--session latest` accepts an incomplete session and says so in the header.
3. Read the "Look at these first" section: those are the SYSTEMATIC_CANDIDATE families, the rows no static rule explains, and start there. Then work down the remaining verdict sections in precedence order. `--fail-on systematic` exits 1 when any such row exists, so the command can gate a follow-up ticket. `--format json` gives the same data sorted for machine use.

What to do per verdict: INFRA, re-run the rows (the probe itself failed). UNSTABLE_BUILD, re-run after builds settle (the live model changed mid-session). FIXTURE_VOCAB, fix or annotate the fixture, not the model. KNOWN_FLAKY, annotate the fixture comment and consider an `A|B` alternation only when both deliveries reach the same handler. CONTESTED, decide with the maintainer, never re-pin to a wrong intent. TOKEN_COLLISION, fix the vocabulary collision in the template. SYSTEMATIC_CANDIDATE, investigate first; a lab round is justified. Every row also carries its raw signals (vocab_status, match_classification, rivals, flips_same_model, history_runs, rebuilt_during_run, failure_kind), so you can see why a lower rule never got a chance.

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

### The `snapshot` subcommand: two producers

The `snapshot` subcommand has two producers for the same file format. Pick by the question you are asking.

#### The name-level producer (default, `jellyfin-names`)

Without `--from-plugin` the subcommand reads item names from a Jellyfin server and writes this format with producer `jellyfin-names` under the `"*"` locale: Series to SeriesName, MusicArtist to JellyfinArtist, MusicAlbum to AlbumName, Movie to MovieTitle, AudioBook to AudiobookTitle, Audio to SongTitle. Connection comes from `--url`/`--api-key` or the environment variables `JELLYFIN_URL` and `JELLYFIN_API_KEY`. Authentication rides the `Authorization: MediaBrowser Token="<key>"` header (verified working on Jellyfin 12 for /Items and /Artists), keeping the key out of URLs that server access logs or error objects might record; Jellyfin 12 also rejects the `X-Emby-Token` header and the lowercase `api_key` query with 401. The key is never printed or written, including in error messages. Item types with no names are omitted so a snapshot can never replace a type with an empty list.

```
python3 scripts/nlu_static/cli.py snapshot --url http://jellyfin.local:8096 --api-key <key> --out catalog-snapshot.json
JELLYFIN_URL=http://jellyfin.local:8096 JELLYFIN_API_KEY=<key> python3 scripts/nlu_static/cli.py snapshot --types Series MusicArtist
```

CAVEAT: this producer is NAME-LEVEL. It does NOT reproduce the plugin's phonetic synonyms, does NOT apply the 140-character slot value truncation, and does NOT enforce the 50000-value cap, so its output understates what catalog sync actually uploads to Amazon. It can also OVERSTATE the live vocabulary in the other direction on a multi-user server: it queries admin-wide, while the plugin's authoritative sync is per-user library-filtered (LibraryFilter.ApplyLibraryFilter), SortName-ordered and capped, so names from libraries the synced user cannot access appear here but never reach Amazon. This producer is ENOUGH when the question reads only the canonical value field: whether a fixture's pinned canonical value exists in the library at all (the JF-954.3 vocabulary check in snapshot mode), the `tokens` CATALOG_VS_MODEL kind (a value equal to a trained literal), or any value-membership question where a synonym cannot change the answer. It is NOT enough the moment a synonym, the truncation or the value cap can change the verdict.

#### The authoritative producer (`--from-plugin`, `plugin`)

With `--from-plugin` the subcommand calls the plugin's own export endpoint and writes exactly what the plugin's catalog sync would upload, because the PLUGIN builds every payload with the same code the sync runs: the per-locale phonetic synonyms, the partial-name synonyms, the seed merges, the 140-character truncation, the same-name dedup, the value cap and the per-user library filtering are all the real ones, not a re-implementation. The response is a plain library read plus computation on the plugin side: it performs no SMAPI call, creates no catalog version and writes no configuration.

```
python3 scripts/nlu_static/cli.py snapshot --from-plugin --user-id <configured-plugin-user-guid> --url http://jellyfin.local:8096 --api-key <admin-key> --out catalog-snapshot.json
python3 scripts/nlu_static/cli.py snapshot --from-plugin --user-id <guid> --locale es-ES --out snapshot-es-ES.json
```

The endpoint is `GET /Plugins/AlexaSkill/Diagnostics/CatalogSnapshot?userId=<guid>[&locale=<locale>]` on the Jellyfin server (admin-gated with the plugin's RequiresElevation policy, so the key must be an administrator's API key; `--user-id` is required and names a user configured in the plugin). A locale appears in the snapshot only when the sync would process it for that user (the CatalogSyncLocales roster, it-IT always, ar-SA never), and a slot type appears only when the sync would upload it for that user (non-empty library; the SongTitle catalog only when that user's song sync is enabled). Values and synonyms arrive in the upload order. One STATED DIVERGENCE to read the locale list with: under a `*` CatalogSyncLocales roster the SYNC expands over the live skill's SMAPI manifest declared locales, while the export (which must not call SMAPI) expands over the embedded 17-model roster, i.e. what a freshly-created skill would carry; a pre-existing skill whose manifest declares fewer locales has a live wiring that is a SUBSET of the snapshot's locale list, so snapshot entries for locales the live skill does not carry are simply never wired there. The export's log line names the roster it resolved and its source. Two more honest signals live in the plugin log, not the response: the export logs the resolved roster per request, and a SongTitle table that hit the value cap announces the cut in the sync's own JF-946 wording ("Song title catalog cut ... snapshot export"), since a cut table is the kept most-played head, not the full distinct set. Authentication and key safety are the same as the name-level producer: the key rides the Authorization MediaBrowser Token header, never a URL, and is never printed or written, including in error text. The producer function refuses a response whose producer is not `plugin` and validates every body through `load_snapshot`'s validator before writing; an empty export (the sync would upload nothing for that user) is a named error, not a schema complaint.

USE THE AUTHORITATIVE PRODUCER for anything involving synonyms or the cap: the fixture-vocab check over a pinned SYNONYM, the triage vocabulary rule on a row whose slot value competes through a synonym family, any question about whether a long title still matches after the 140-character truncation, anything sensitive to the 50000-value cut or to which locales/types the sync actually covers for that user. When in doubt, run the authoritative producer: it is a superset of the truth the name-level one approximates, and the tools treat both identically once the file is written.

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

## The `graph` subcommand: the sample-subsumption graph (JF-954.4)

Most NLU regressions here are intent competition: a short carrier with a free-text slot in one intent can also parse a phrase that belongs to a more specific intent, and Amazon's trainer then decides by weights nobody can see. The `graph` subcommand computes, deterministically, which rival READINGS exist. It writes one `graph_<locale>.json` per locale: nodes are intents, and a directed edge A to B exists when at least one sample of B has a generated reading that a sample of A can also produce, meaning everything B's sample can say, A's sample can also claim.

### The pattern rules, in plain words

Each sample compiles into a PATTERN: a sequence of literal tokens (the shared `tokenize` over the text between `{slot}` placeholders) and slot items, each slot carrying its class from the pinned classification table (FREE, NUMERIC, STRUCTURED, CLOSED with its type name). Each sample also compiles into a WITNESS: the abstract utterance it generates, with every slot collapsed to one class token such as `<FREE>` or `<CLOSED:SeriesName>`. A slot type the table does not list at all (any unlisted `AMAZON.*` builtin) is treated as FREE for behavior and its name is reported in the graph's `unclassified_types`, so the table gets maintained instead of silently guessing.

Pattern A matches witness W when the whole of W consumes left to right: a literal matches the identical literal; a FREE slot matches one or more consecutive items of ANY kind; a NUMERIC slot matches `<NUM>` only; a STRUCTURED slot matches the class token of the same type only; a CLOSED slot of type T matches a `<CLOSED:T>` token, or a `<CLOSED:T2>` token when the normalized entry token lists of T and T2 intersect, or a run of literal tokens equal to the token list of some entry of T. The implementation is a memoized dynamic program over (pattern index, witness index), never a regex: vocabulary alternations would blow up.

### Reading an edge, and the universal intents

Edge A to B carries `kind`: IDENTICAL when the two samples have identical skeletons (slots compared by class and, for CLOSED, by type), SWALLOW otherwise; `swallowed` and `total` count B's samples (matched by at least one A sample, out of all of them); `examples` lists up to 3 qualifying `[a_sample, b_sample]` pairs, the first three in sorted order. `universal` lists the intents that swallow at least one sample of more than half of the other intents, which is what a bare `{query}` carrier looks like in the graph.

The graph is noisy by nature: free-text intents swallow a lot, and that is the truth the graph is asked to record, not a defect. It deliberately runs NO validator phase; the tool belongs in review and triage, not in blocking CI.

```
python3 scripts/nlu_static/cli.py graph build --locale es-ES --out /tmp/graphs
python3 scripts/nlu_static/cli.py graph build --all --out /tmp/graphs
python3 scripts/nlu_static/cli.py graph diff old_graph.json new_graph.json
python3 scripts/nlu_static/cli.py graph diff-git --base HEAD~1 --locale es-ES
```

`graph diff` prints added edges, removed edges and edges whose `kind` or `swallowed` changed, each group sorted, and always exits 0 unless `--fail-on-new` is passed, which exits 1 only when an edge was ADDED (new competition introduced; removed or changed edges are informational).

### The reviewer workflow: diff-git before committing a template change

When a change touches the locale templates (`Alexa/InteractionModel/templates/*.yaml`), run `graph diff-git --base <the ref you branched from> --locale <locale>` (or `--all`) BEFORE committing. It builds the old graph from `git show <ref>:<model path>` and the new one from the working tree and prints the same diff. An `added` line names competition the change introduces that did not exist before: judge each one. A new SWALLOW edge from a free-text carrier onto a previously uncontested intent is a warning sign the new samples make that intent's rows marginal (flippable across rebuilds, the JF-945 class); a new IDENTICAL edge is usually an outright mistake, since two intents claiming the exact same phrasing can only be separated by trainer weights. Removed edges and changed `swallowed` counts report competition the change relieved or resized and are usually good news. Route deliberate new competition to a fixture row comment (mark the row as contested) instead of blocking the change; the triage report of JF-954.6 consumes the classification below and will carry the same signal per red row.

### The `match` and `classify-fixtures` subcommands

`match --locale X "utterance"` matches a REAL utterance (plain tokens) against every sample pattern and prints, per matching intent, its most specific hit: `intent`, `sample`, `specificity` (the number of literal tokens in the sample), sorted by specificity descending then intent name. The utterance-mode rule table deliberately over-approximates two classes: a NUMERIC slot stands for exactly one token of any kind, and a STRUCTURED slot for one to four tokens of any kind, so "espera mucho" has a reading under "espera {n} segundos" too. A hit therefore means "a reading exists", never "Amazon will choose it".

`classify-fixtures --locale X|--all [--snapshot f.json]` classifies every committed fixture row into three classes and prints sorted JSON rows with exactly the fields `locale`, `utterance`, `expected_intent`, `classification`, `rivals`, `rival_at_least_as_specific`:

- `UNCONTESTED`: only the intents the row names in `expected_intent` (an `A|B` alternative list is honored) have a matching sample. A red row here is NOT marginal: it deserves a real investigation.
- `CONTESTED`: an expected intent matches and at least one other intent matches too. `rivals` lists the others with their samples and specificities; `rival_at_least_as_specific` is true when the best rival is at least as specific as the best expected hit, which marks the row as marginal by construction, flippable across rebuilds.
- `NO_SAMPLE_MATCH`: no expected intent has a matching sample; the row relies on trainer generalization or on vocabulary absent from the effective model (for example a catalog-backed slot whose value only the library snapshot carries). `rivals` lists whatever else matched.

```
python3 scripts/nlu_static/cli.py match --locale es-ES "pon breaking bad temporada uno episodio tres"
python3 scripts/nlu_static/cli.py classify-fixtures --locale es-ES
python3 scripts/nlu_static/cli.py classify-fixtures --all --snapshot catalog-snapshot.json
```

## The `carriers` subcommand: does this candidate sample already exist (JF-970)

`carriers` answers the model-content question behind every "add this sample" premise: does the candidate SAMPLE already exist in a locale, on which intent, and under which carrier. A CARRIER is a sample with every `{slot}` placeholder reduced to ONE anonymous token, so `play some {genre}` and `play some {musician}` are the identical carrier even though their slot types differ; two samples share a carrier exactly when Alexa could read them as the same pattern with different slot vocabulary. Normalization reuses the shared normalizer (casefold, NFC, punctuation at token edges stripped) and the ONE placeholder regex; slot NAMES never participate, only slot position count.

Caveat first: `match` is NOT a presence check for catalog-slotted samples. It takes a real utterance and only fires where the spoken value sits in the slot vocabulary, so `match --locale en-US "play some jazz"` lists PlayByGenreIntent and PlaySongIntent and never reveals that en-US also carries `play some {musician}` on PlayArtistSongsIntent (slot type JellyfinArtist); when you are about to premise a task on "add `play some {genre}`", ask `carriers`, not `match`.

Verdicts, per (candidate, locale):

- `PRESENT`: an existing sample of the NAMED intent normalizes to the identical carrier (`present` carries the intent and the existing sample text). Only checked when `--intent` names one; an unnamed candidate reports only `SAME_CARRIER`/`ABSENT`.
- `SAME_CARRIER`: another intent owns the identical carrier. `same_carrier` lists EVERY hit with the owner intent, its sample text and, per placeholder, the slot name, declared slot type and whether that type is catalog-backed (the six CATALOG types the sync replaces with library values; a candidate colliding with a catalog-backed slot behaves very differently from one colliding with a STATIC type).
- `ABSENT`: no identical carrier anywhere in the locale model.

A `PRESENT` row may still list cross-intent `same_carrier` hits: the carrier is owned twice, which is itself worth knowing before you premise an edit on it.

Input forms, combinable: inline candidates (`--sample` repeatable, `--intent` optional) with `--locale LOCALE` or `--all` (expand across every committed locale), or `--file` with a JSON list of `{locale, intent, sample}` rows or a TSV with the three tab-separated columns `locale<TAB>intent<TAB>sample` (an empty intent column means "unnamed"; `#` comment and blank lines are skipped). A locale of `*` in a file row expands to every committed locale, the snapshot file's own glob convention. Exit code is 0 for every verdict including `ABSENT` (this is a lookup, not a lint); exit 2 only for refusals (no candidates, unknown locale, unreadable model or file).

Output is deterministic: candidates in input order, locales sorted within a candidate, `same_carrier` hits sorted by intent then sample. `--format json` (default) prints one verdict row per candidate per locale; `--format text` prints the human table with the slot evidence indented under each hit.

ja-JP convention: the locale has no word spaces, so the carrier there is plain character-level comparison after slot reduction (the placeholder becomes the anonymous marker, whitespace is dropped entirely, since the locale is defined by `NON_WHITESPACE_TOKENIZED_LOCALES` as one where word spaces carry no meaning). This follows the `fixtures` subcommand's precedent of answering ja-JP without word tokens instead of printing the SKIP line; the token-dependent tools (`tokens`, `graph`, `match`) still skip ja-JP, and `carriers` deliberately does not.

```
python3 scripts/nlu_static/cli.py carriers --locale en-US --intent PlayByGenreIntent --sample "play some {genre}"
python3 scripts/nlu_static/cli.py carriers --all --intent PlaySongIntent --sample "play {song}"
python3 scripts/nlu_static/cli.py carriers --file candidates.tsv --format text
```

### Limits: what the graph does NOT tell you
The graph computes rival readings, nothing else. It does NOT predict which intent Amazon's trainer picks, does NOT weight edges by anything other than sample counts, and does NOT model ASR, entity resolution ranking, or the default-music-service arbitration. A SWALLOW edge says the phrasing is contestable in principle; whether the trainer actually ever picks the rival is a live question for profile-nlu, simulate-skill or a device test. This exact over-trust failure already happened once: in an es-US lab ablation on 2026-11-23, removing five bare PlaySong carriers moved song requests that used UNTOUCHED carriers ("escucha", "quiero escuchar") to PlayArtistSongsIntent. The graph showed PlayArtistSongsIntent as a neighbour all along; it could not and did not predict that the trainer would move the untouched carriers' traffic. Read an edge as "this row is marginal", never as "this edge is the cause of the red". Also note the graph reads the SAVED model only: utterances whose routing depends on catalog values the model never sees are invisible to it until a `--snapshot` overlays one.

## The `ledger` subcommand: the results ledger and per-row flip statistics

The live NLU suite (`./scripts/run_nlu_tests.sh` without `--dry-run`) writes a RESULTS LEDGER automatically (JF-954.5): one JSON Lines file per suite session under `tests/integration/results/` (gitignored, local only), named `nlu-<UTC yyyymmddThhmmssZ>-<pid>.jsonl`. Dry-run writes nothing; the environment variable `NLU_LEDGER=0` disables the ledger and any other value or unset leaves it enabled; `NLU_LEDGER_DIR` overrides the output directory. Skipped rows are not recorded. The ledger never changes a test outcome: any exception inside ledger code is caught, reported once as a pytest warning, and the suite continues without a ledger.

Four record types, one JSON object per line, each line flushed as written so an interrupted run keeps its rows; a file without a `session_end` record is an INCOMPLETE session whose rows still count in the statistics (a session that recorded no rows at all is removed at close):

- `session_start`: `schema` (1), `session_id` (the file stem), `started` (UTC ISO 8601), `skill_id`, `git_head`, `git_dirty`, `locales` (empty at write time; the `locale_start` records that follow fill it lazily).
- `locale_start`, written the first time a locale is exercised: `locale`, `model_sha256` (sha256 of the bytes of the committed `model_<locale>.json`), `live_identity_start` (the interaction-model eTag live on the skill for that locale, read through one read-only `get-skill-status` call; null when unobtainable).
- `row`: `locale`, `utterance` (verbatim), `expected_intent` (verbatim, may contain `|`), `expected_slots` (verbatim from the fixture row: the empty object for any-value expectations and for fixtures with no slot expectations, the pinned value object otherwise), `actual_intent` (string or null), `slots` (resolved slot name to value string), `outcome` (`pass`, `fail`, `error`), `failure_kind` (null, `intent`, `slot_missing`, `slot_empty`, `slot_value`, `smapi_error`), `detail` (short string or null). The SMAPI error path and the suite's per-test timeout are environmental and map to outcome `error`; the four assertion failures map to the four slot/intent kinds. Storing `expected_slots` verbatim keeps every session self-sufficient: it stays analysable after fixture files change.
- `session_end`: `ended`, `rows`, and `live_identity_end` (per exercised locale). At most two read-only SMAPI identity calls are made per locale per session, one at locale start and one at session end.

`rebuilt_during_run` (per session locale in the stats output) is true when both live identities exist and differ, meaning a rebuild happened DURING the session and flip reasoning across it is suspect; it is null when either identity is unobtainable.

```
python3 scripts/nlu_static/cli.py ledger stats
python3 scripts/nlu_static/cli.py ledger stats --locale it-IT --last 10
python3 scripts/nlu_static/cli.py ledger stats --format text
python3 scripts/nlu_static/cli.py ledger list
```

Statistics order sessions by `started` (ties by `session_id`) and key rows by `(locale, normalize_text(utterance))`. With `--locale`, the sessions are first restricted to those that exercised that locale, so `--locale it-IT --last 10` means the newest ten it-IT sessions, not the newest ten sessions of any locale. Per key it reports `runs`, `passes`, `fails`, `errors`, `actual_intents` (sorted distinct values), `flips_total`, `flips_same_model`, `last_outcome`, `last_actual_intent`, `first_session` and `last_session`. The two flip counters are the point of the ledger:

- `flips_total` counts adjacent SESSION pairs of the key whose `actual_intent` differs. Each session contributes ONE representative row, the last non-error row it recorded for the key, so a duplicated utterance inside one session never flips against itself and a session whose rows are all `error` represents nothing: a SMAPI outage between two passes is never a flip.
- `flips_same_model` counts the same pairs restricted to those where the locale's committed `model_sha256` is equal AND the live identities are present, equal, and stable within both sessions (a session with a rebuild during the run did not run one model, so its pair is unproven): a flip counted there happened with NO template or live-model change, which is the JF-945 trainer-nondeterminism class. Any unproven pair, including one whose sessions lack the model sha or the live identity, stays in `flips_total` and out of `flips_same_model`, so a `flips_same_model` of zero means "no PROVEN same-model flips"; read the per-session `model` and identity fields to tell a genuinely stable row from an unclassified one.
- `last_outcome` and `last_actual_intent` come from the row's last NON-error record: a session-ending SMAPI outage is environmental noise, not the row's routing verdict, and both fields are null when every recorded row for the key is an error.

Reading rule: a red row with `flips_same_model` of zero over many runs deserves investigation; it was stable for many builds and is now red, so the change is a signal. A red row with a history of same-model flips is trainer noise; annotate such a row in its fixture comment rather than chasing it. The statistics describe what the ledger recorded; like every tool on this page they do not predict how Amazon's trainer will route the next run.


## The `triage` subcommand: the automatic triage report (JF-954.6)

`triage` reads one ledger session (default: the latest COMPLETE session in the results directory; `--session latest` accepts an incomplete one and the header says so; `--session <id>` picks one explicitly) and classifies every red row (outcome `fail` or `error`) with the fixed seven-verdict precedence, implemented as one ordered list of (verdict, predicate) pairs in `scripts/nlu_static/triage.py`: INFRA (the probe errored), UNSTABLE_BUILD (the locale's live identity changed mid-session), FIXTURE_VOCAB (the JF-954.3 check reports ABSENT or FIXTURE_SLOT_UNKNOWN; in committed mode a plain ABSENT on a CATALOG type means only "not in the committed seed" and does NOT fire, while under a snapshot it does and the detail says `absent in live catalog`), KNOWN_FLAKY (at least `--flaky-min-flips` same-model flips among the last `--history` sessions BEFORE the chosen one; a row with fewer than two prior runs is never KNOWN_FLAKY and carries `history: insufficient`), CONTESTED (the matcher classification is CONTESTED or NO_SAMPLE_MATCH and the row's actual_intent is one of the rivals), TOKEN_COLLISION (a CARRIER_VS_VALUE finding whose carrying intent is expected, whose type the actual intent uses, and whose entry occurs in the utterance), SYSTEMATIC_CANDIDATE (none of the above: look at these first). ja-JP rows skip the matcher and collision rules with the explicit note `static signals skipped: ja-JP`. The `ABSENT_IN_LIBRARY_SEED_ONLY` status never fires the vocabulary rule; it rides the row's `vocab_status` signal. The vocabulary rule reads the row's `expected_slots` from the LEDGER record, never from the fixture files.

Families group red rows by (language prefix, expected_intent, actual_intent, verdict), sorted by size descending then key; a family lists its locales and its rows in a short table (locale, utterance, expected, actual, detail). The markdown body starts with "Look at these first" (the SYSTEMATIC_CANDIDATE families) and continues with one section per other verdict in precedence order, ending with a fixed limits paragraph. The JSON form carries the same data, sorted, for machine use. Output is byte-identical for the same ledger files, models, baseline and snapshot; no current time is printed. Exit code 0 by default; `--fail-on systematic` exits 1 when at least one SYSTEMATIC_CANDIDATE row exists. A locale whose effective model cannot be built is reported with an explicit `SKIP <locale>: ...` line on stderr.

```
python3 scripts/nlu_static/cli.py triage --snapshot catalog-snapshot.json
python3 scripts/nlu_static/cli.py triage --session latest
python3 scripts/nlu_static/cli.py triage --dir /tmp/ledger --format json
python3 scripts/nlu_static/cli.py triage --fail-on systematic --out triage.md
```

Like every tool on this page, the verdicts describe necessary conditions and recorded history; they do not predict how Amazon's trainer routes an utterance. The header also says which model mode was used: committed mode can only answer the committed-seed question, so re-run with a snapshot when none was given.

## The `identity` subcommand: canonical model identity (JF-954.7)

The `identity` subcommand prints one CANONICAL identity hash per locale: the sha256 of the committed `languageModel` serialized as canonical JSON (keys sorted, compact separators, UTF-8, no ASCII escaping). Two models that differ only in key order, file whitespace or wrapper shape hash equal; a changed sample, slot, slot type, type value or synonym changes the hash. Sample ORDER is part of the hash on purpose: a reorder is a model change and must show up. With `--snapshot` each locale also gets `effective_sha256`, the same canonical hash over the effective model: catalog-backed types replaced by the snapshot entries, entries sorted so the snapshot's own entry order never changes the hash, and a snapshot naming only STATIC types leaves the hash at the committed value. Unlike the token tools, identity needs no word tokens, so ja-JP is hashed like every other locale and there is no SKIP line here. Related but different: the ledger records the raw-bytes sha256 of the model FILE from the suite side (JF-954.5); the canonical hash is the formatting-independent identity to quote in tickets, notes and comparisons between refs.

```
python3 scripts/nlu_static/cli.py identity --locale es-ES
python3 scripts/nlu_static/cli.py identity --all --format text
python3 scripts/nlu_static/cli.py identity --locale es-ES --snapshot catalog-snapshot.json
```

## The `select` subcommand: change-driven fixture selection (JF-954.7)

`select --base <git-ref>` answers the fast-loop question: which NLU fixture rows can a model change since that ref affect? Per locale it compares the model at the ref (read through `git show`) against the working tree, in four steps: hashes equal means the locale is UNCHANGED and contributes no rows; otherwise it computes the CHANGED INTENTS (intents added or removed, samples or slots differing, every intent declaring a slot whose TYPE changed values or synonyms, and every intent of the locale when the invocation name differs); unless `--no-neighbours`, it adds the NEIGHBOURS, meaning every intent edge-connected in either direction to a changed intent in the subsumption graph of the OLD model or of the NEW model; and it selects every fixture row of the locale whose `expected_intent` alternatives (the `A|B` form included) intersect the selected set. ja-JP has no sample graph (no word spaces, the shared skip convention), so a changed ja-JP locale selects ALL of its intents and the payload carries a note saying so. A locale whose model file does not exist at the base is a locale added after the base: every intent counts as changed.

Safety rule: selection never silently drops coverage. When anything prevents a precise answer for a locale (the base model unreadable, the working-tree model unreadable, a graph build error, hashes that differ with no attributable intent-level change such as a type value no intent uses), that locale falls back to ALL of its rows with `"status": "fallback_all"` and a reason. A malformed working-tree model is exactly the mid-template-edit state the fast loop serves, so it selects everything for that locale instead of silently deselecting it. The output JSON is schema 1 with exactly `schema`, `base` (as given), `locales` and `totals`; `totals.rows_selected` counts DISTINCT selected utterances (the sum of the payload `rows` lengths) while `totals.rows_total` counts fixture entries, and both are byte-identical across runs over the same inputs; `--format text` prints one summary line per locale plus the totals.

```
python3 scripts/nlu_static/cli.py select --base main --all --out /tmp/rows.json
python3 scripts/nlu_static/cli.py select --base 5e870594~1 --locale it-IT --format text
python3 scripts/nlu_static/cli.py select --base HEAD~1 --locale es-ES --no-neighbours
```

The it-IT line of the second example is the JF-945 opener seen through the tool: the one added sample marks PlayPodcastIntent changed, the graph neighbours (PlayByGenreIntent, PlaySongIntent, PlayVideoIntent) join through the old and new edges, and the selection covers the podcast rows plus those rivals' rows while every untouched locale stays unchanged.

### The fast loop, and why the full suite is still the release gate

The recipe:

```
python3 scripts/nlu_static/cli.py select --base main --all --out /tmp/rows.json
NLU_ROW_FILTER=/tmp/rows.json ./scripts/run_nlu_tests.sh
```

`NLU_ROW_FILTER` points at a file produced by `select`; the suite then runs only the listed (locale, utterance) pairs. Items not listed are DESELECTED through pytest's own mechanism, so the summary shows the deselected count, and one line at collection states how many rows were kept and from which file. Matching compares utterances after the shared normalizer, so case, accent composition and whitespace fold away. E2E, reliability, smoke and every other non-NLU test is never touched; the checkpoint/resume flags compose with the filter (checkpoint-skipped items leave the list before the filter runs). A filter file that cannot be read or does not pass the schema check ABORTS the session with a clear error naming the file: silently running everything or silently running nothing would both be wrong. The pure decision function and the loader live in `scripts/nlu_static/selection.py` (`should_keep`, `load_row_filter`) and are unit-tested in `tests/scripts/`; the conftest hook only calls them.

THE FULL SUITE REMAINS THE RELEASE GATE. Selection follows the model text, and the trainer does not: in the es-US lab ablation of 2026-11-23, removing five bare PlaySong carriers moved song requests that used UNTOUCHED carriers ("escucha", "quiero escuchar") to PlayArtistSongsIntent, a shift no static analysis could connect because the moved carriers' samples never changed and no edge pointed at them from the change. Run the filtered suite while iterating on templates; run the full suite before a release, before merging a model change to main, and whenever a catalog sync or a model rebuild has happened in the middle of any run. A live filtered session also leaves a trace in the results ledger (a `row_filter` record with the filter path and the kept/total counts), so the ledger history distinguishes a filtered run from a full-coverage one; the trace is best-effort and can never fail the session.
