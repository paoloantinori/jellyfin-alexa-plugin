---
id: JF-643
title: >-
  JF-643 - katakana query values never match Latin library names (script gap in
  fuzzy/phonetic search): naturalized ja-JP artist and genre requests all end
  not-found
status: To Do
assignee: []
created_date: '2026-09-27 06:38'
updated_date: '2026-09-27 09:08'
labels:
  - search
  - i18n
  - ja-JP
  - device-found
dependencies:
  - JF-642
references:
  - >-
    backlog/tasks/jf-642 -
    JF-642-ja-JP-noun-qualified-artist-carriers-stolen-by-PlayByGenre-the-free-text-genre-capture-even-canonical-pre-existing-forms-route-to-genre-artist-intent-unreachable-by-noun-voice.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 from the JF-642 investigation (same-turn rule; the mechanism split is documented there).

PROBLEM (simulator-verified on minix, ja-JP locale): katakana query values cannot match Latin library names on ANY search path, because the fuzzy/phonetic layers (FuzzyStrings + Double Metaphone) are Latin-script algorithms:
- PlayArtistSongsIntent musician='クイーン' (Queen) -> artist not-found.
- PlayByGenreIntent genre='ジャズ' (Jazz) -> NotFoundGenre (raw katakana vs the library's Latin 'Jazz').
- The cross-media artist fallback (TryEntityFallbackAsync) fires on the genre steal but fails the same way.
Latin values on the same paths work ('queen', 'Jazz' both resolve/play), so this is purely the script gap. Japanese ASR transcribes foreign artist/genre names as katakana, so the naturalized ja voice hits this on every foreign-name request. The same class will affect any non-Latin-script locale value set (notably ar-SA, hi-IN native script values) - check those while in here.

WHERE: the search entry points that receive raw slot text. ArtistSearch (Alexa/Util/ArtistSearch.cs) + SongNgramIndexService/KeywordMatcher for titles + the genre query in PlayByGenreIntentHandler (Genres array) + CrossMediaFallback.TryEntityFallbackAsync. A query-side normalization (katakana -> romaji transliteration, deterministic syllabary mapping; long-vowel ー, small tsu っ, geminate handling, ノ generative-phonetics rules note JF-379 already owns velar-stop rules for ja) applied BEFORE fuzzy/phonetic scoring would bridge all paths at once. Do NOT transliterate library-side values; the gap is query-side.

VERIFICATION BAR: simulator probes under ja-JP: musician='クイーン' plays Queen (or the expected disambiguation), genre='ジャズ' plays jazz. Unit tests pin the transliterator (katakana string -> expected romaji) and the search-path integration (query katakana, candidate Latin -> match above threshold). No regression in Latin/Latin matching.

OUT OF SCOPE: the routing-layer genre steal (JF-642 owns it; this task is its companion - fixing routing without this still leaves katakana artist voice at not-found, and vice versa).

RELATION: depends on nothing technically; JF-642 dependency recorded only for scheduling coherence (same locale, same evidence base). Both must land for naturalized ja artist voice to work end-to-end.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes (2026-09-27)

CHOKE POINTS wired (query side ONLY; candidates, titles, and catalog values are never transliterated):

1. `Alexa/Util/KatakanaRomanizer.cs` (NEW): kana-to-romaji, katakana + hiragana (folded via the constant +0x60 offset), digraph pair table, dictionary lookup per char, no regex; no-kana input returns the SAME instance (fast no-op, pinned by Assert.Same).
2. `FuzzyMatcher` (the fuzzy sub-family, one file): the QUERY argument of `FindBestMatchWithScore` (plain + phonetic), `RankMatches`, and `Score` is romanized at entry; the phonetic overload encodes the romanized form. The two `FindBestMatch` wrappers delegate and inherit. Covers SearchService.FuzzyMatch/FuzzyMatchPhonetic, HandleFuzzyMiss's matcher, PassesArtistMatchAcceptance, FindBestNonEmbeddedMatch, SearchItemsFuzzyAsync's matcher, and the PlayFavorites/PlayPodcast/SearchMedia/PlayArtistSongs direct calls (first-arg-is-query verified at every call site before wiring).
3. `ArtistSearch.SearchAsync` entry: both implementations' tiers, including the database SearchTerm/NameStartsWith/NameContains branches.
4. `PlayArtistSongsIntentHandler` entry: the JF-382 inline tier chain bypasses SearchAsync, so it romanizes its own musician local.
5. `CrossMediaFallback`: `TryEntityFallbackAsync` entry (word guard + SearchAsync + phonetic confirm + word-coverage valve) and `TrySongFallback` entry (tokens feed the n-gram index).
6. `AlbumPlayService`: `BuildPlaylistPlayResponseAsync` entry + `TryAlbumFallbackAsync` entry.
7. `SearchService.SearchItemsFuzzyAsync` entry (the JF-508/JF-526 coverage gate tokenizes the query after the matcher).
8. `BaseHandler.HandleFuzzyMiss` entry (its coverage gate tokenizes the query).
9. Slot-string entries feeding SearchTerm/Tokenize/Genres filters: PlayByGenre (genreQuery local, see the tier below), PlaySong songQuery, PlayAlbum album + musician (the musician also feeds the JF-489/JF-492 title retries), PlayVideo titleQuery, PlayBook book, PlayPodcast podcastName, PlayRadio stationQuery, FindSong SearchAndRespondAsync keywords local, SearchMedia query.
10. `SongNgramIndexService`: NO change (receives pre-tokenized keywords; every caller romanizes the string before tokenizing, so index lookups, KeywordMatcher.Score/ScorePhonetic, and query-side DoubleMetaphone all see romaji against the Latin-built index).

GENRE RESOLUTION TIER (beyond bare romanization, required by this task's verification bar): the server-side Genres filter is exact CleanValue equality (documented in RadioTrackSource's contract note), so 'ジャズ' romanized to 'jazu' still misses the tag 'Jazz'. PlayByGenre now runs, ONLY when the raw slot contained kana (`KatakanaRomanizer.ContainsKana` gate; Latin queries keep byte-identical behavior, pinned by two tests), a one-query genre-vocabulary fetch (Genre + MusicGenre kinds, Limit 500, cheap DTO) matched through FuzzyMatcher's phonetic overload with on-the-fly Double Metaphone codes ('jazu' and 'Jazz' both code JS; the length-banded floor 91 clears the default 60 threshold), then re-queries with the canonical tag. No match falls through to the existing JF-463 artist fallback and NotFoundGenre unchanged. No thresholds changed, no config flags added.

ROMANIZATION DECISIONS (deterministic, pinned by KatakanaRomanizerTests):
- long-vowel mark ー: CONTRACTION, the mark is dropped. 'クイーン' to 'kuin'; DoubleMetaphone('kuin') equals DoubleMetaphone('Queen') (both KN), the load-bearing property, tested with the production helpers. Doubling the vowel ('kuiin') encodes the same code but sits one letter worse for Levenshtein and the phonetic-floor length band, so contraction is the pinned choice.
- sokuon っ/ッ: doubles the next syllable's leading consonant ('ロック' to 'rokku'); 't' before a 'ch' syllable ('マッチ' to 'matchi', Hepburn); dropped when dangling or before a vowel.
- moraic nasal ん/ン: always 'n'.
- hiragana: same syllable values (code-point fold); kanji and every other non-kana character pass through unchanged (no dictionary; accepted, a kanji run stays unmatched).
- digraphs: y-digraphs (キャ through ピョ), シェ/ジェ/チェ, ファ/フィ/フェ/フォ/フュ, ヴァ/ヴィ/ヴ/ヴェ/ヴォ, ウィ/ウェ/ウォ, ティ/ディ/トゥ/ドゥ.
- Hepburn choices: シ shi, チ chi, ツ tsu, フ fu, ジ ji, ヲ o, ヰ i, ヱ e.
- speech trade: sites that reassign the query local may speak the romanized form in not-found/disambiguation prompts (TryEntityFallbackAsync, HandleFuzzyMiss, PlaySong/PlayAlbum/PlayVideo/PlayBook/PlayPodcast); sites with a dedicated search local keep the user's raw words in speech (PlayByGenre, PlayRadio, FindSong).

VERIFICATION (2026-09-27, this worktree):
- `dotnet build Jellyfin.Plugin.AlexaSkill.sln`: 0 errors (the only 2 warnings are the pre-existing xUnit1013 pair on the untouched SetPlaybackSpeedIntentHandlerTests).
- `dotnet test Jellyfin.Plugin.AlexaSkill.Tests`: `Passed!  - Failed: 0, Passed: 4496, Skipped: 0, Total: 4496` on BOTH net9.0 and net10.0 (28 new test cases: 22 romanizer incl. 16 theory rows, 3 ArtistSearch integration, 3 PlayByGenre handler).
- `python3 scripts/validate_interaction_models.py`: PASS (278 warnings, the pre-existing baseline; no model/template files touched).
- WarmingGateCoverageTests: no roster change needed (no handlers added).

SAME-CLASS RESIDUALS (documented, out of scope here): PlayRadio's station-to-genre seed keeps exact-match semantics (romanized only; a katakana radio genre would need the same vocabulary-resolution tier if ja radio voice matters); queries containing kanji romanize only their kana runs; the mood slot's LocalizedMoodMap (katakana mood words) is the mood architecture, not the search layer.
<!-- SECTION:NOTES:END -->

<!-- SECTION:NOTES:SIMPLIFY-ROUND:BEGIN -->
/simplify round (2026-09-27, orchestrator, all four angles): applied = shared TestCandidate (deletes the fifth per-file copy that shadowed the JF-573 shape), NormalizeQuery helper collapsing the three verbatim FuzzyMatcher sites, Romanize null-preservation via NotNullIfNotNull (collapses the guarded handler sites), FindSong passes the already-romanized local to TryEntityFallbackAsync (kills the duplicate transliteration), TryAlbumFallbackAsync romanize-before-guard matching its sibling, the stale RAW comment fixed, Romanize made internal matching ContainsKana, and the class-doc NARROWING record. Skips recorded: no loop-merge into KeywordMatcher.Tokenize (it is index-side shared: would transliterate library text); the yoon table stays declarative (auditable over derived); the ~15 per-site comments stay (per-site placement contracts are the repo convention).

INTERPLAY with JF-642 (recorded on both sides): ER_SUCCESS_MATCH canonical (JF-642's custom GenreType) bypasses the vocabulary tier entirely and exactly; the tier is the ER_NO_MATCH / long-tail / library-specific-tag path. Neither makes the other redundant: the static type covers the common head with zero DB queries, the tier covers the dynamic library vocabulary.

KNOWN NARROWING (accepted, review-verified): normalization is asymmetric (query only). A kana query against a KATAKANA-TAGGED library name, which previously exact-matched on the Contains tier, now misses on every tier (romanized 'kuin' cannot equal the kana name; Double Metaphone keeps kana, so the phonetic floor cannot rescue it). Latin-tagged libraries (the common shape) are unaffected. The deeper fix is symmetric index-side normalization at index-build time; tracked with the residuals below.

SAME-CLASS RESIDUALS (extends the worker's list; /simplify reuse angle): un-wired genre slots with identical exact-Genres semantics: PlayRandomIntentHandler, PlayByDecadeIntentHandler, BrowseLibraryIntentHandler. Un-wired raw-kana SearchTerm sites: AddToQueueIntentHandler (song side; artist side covered), PlayNextIntentHandler, AddSongToPlaylistIntentHandler, PlayChannelIntentHandler, TvNextUpService. When the first sibling genre site is wired, lift ResolveGenreTagAsync onto the SearchService collaborator (the GetArtistSongsAsync precedent) instead of copying it. Script coverage: this romanizer is kana-specific by construction; Devanagari (hi-IN) and Arabic (ar-SA) native-script values remain unmatched (the JF-643 task Description flagged them); if a second script lands, extract a normalizer-chain shape rather than a parallel one-off.

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-27 LIVE VERIFICATION OUTCOME (deployed a39eb2a8, minix): genre rows PASSED (ジャズ plays jazz via the vocab tier; Jazz Latin control unchanged; the stolen-artist rescue fires). Artist rows FAILED the bar as written: クイーン -> 'kuin' -> tier-4 phonetic floor 91 TIE between Queen and Keane resolves silently to Keane (single-best auto-play, no disambiguation); ビートルズ -> 'bitoruzu' -> plain-fuzzy false accept of 'Sator' at threshold 60. Root cause and fix direction filed as JF-652 (kana-aware acceptance calibration), which BLOCKS this task's completion. Mechanism itself verified good: romanizer no-op on Latin (byte-identical, pinned), kana bridging works, genre path exact. Status stays In Progress until JF-652 lands and the artist rows re-verify.
<!-- SECTION:NOTES:END -->
