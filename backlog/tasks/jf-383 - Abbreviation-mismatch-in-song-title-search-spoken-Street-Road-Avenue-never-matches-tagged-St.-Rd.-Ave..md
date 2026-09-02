---
id: JF-383
title: >-
  Abbreviation mismatch in song title search: spoken "Street"/"Road"/"Avenue"
  never matches tagged "St."/"Rd."/"Ave."
status: Done
assignee: []
created_date: '2026-08-20 06:40'
updated_date: '2026-08-20 09:28'
labels:
  - bug
  - search
  - song-search
  - asr
  - tokenizer
  - i18n
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/19'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
LIVE REPRO 2026-08-20 (user report + log analysis):

User asked Alexa (it-IT) for "Decature Street" by The Twilight Singers. Three failures:
1. PlaySong corr=658e6bc9: ASR transcribed "Decature Street" as "the cater street". Exact Jellyfin SearchTerm query -> 0 songs.
2. FindSongByArtist corr=a4b0938c: artist resolved correctly to The Twilight Singers (id 062bd039, 33 songs).
3. FindSong corr=afe3b57e with keyword="street": 0 matching songs. ROOT CAUSE: the library track is titled "Decatur St." (abbreviated). KeywordMatcher tokenizes on punctuation -> title tokens [decatur, st]; the keyword token "street" matches neither exactly nor phonetically (DM: street->STRT vs st->ST).

ROOT CAUSE CLASS: orthographic abbreviation mismatch (St.<->Street, Rd.<->Road, Ave.<->Avenue, No.<->Number, Pt.<->Part), NOT phonetic drift. Distinct from the Koop/cup problem: Double Metaphone cannot bridge it (same word, different orthographic realizations). The catalog synonym layer (JF-379) does not help either: this is query/index-side title matching.

Music taggers commonly abbreviate street addresses and track parts in titles, so this recurs for every abbreviated title.

LIKELY FIX (per session analysis 2026-08-20): abbreviation expansion at tokenization - a small canonical map applied in KeywordMatcher.Tokenize (or as synonyms in SongNgramIndexService's index) so a spoken "street" matches a tagged "st." token (and vice versa). Keep it bidirectional and bounded to the common music-title abbreviations. Files: Alexa/Util/KeywordMatcher.cs, Alexa/SongNgramIndexService.cs, handlers that tokenize titles/keywords (FindSongIntentHandler 3-stage search).

VERIFICATION CASE: library track "Decatur St." (The Twilight Singers) must be found by (a) FindSong keywords="street" with artist filter; (b) ideally also the full "decatur street" phrase. Simulator-testable locally (no hardware).

Also noted during repro (secondary, not this task): the catalog contains folder-derived artist entries ("The Twilight Singers - She Loves You" with 0 own songs) that pollute entity resolution. Separate cleanup if it recurs.

Upstream GitHub issue: see the corresponding issue in paoloantinori/jellyfin-alexa-plugin (linked in comments once created).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 #1 Abbreviation map (street/st, road/rd, avenue/ave, number/no, part/pt at minimum) applied in title/keyword tokenization so a spoken full word matches an abbreviated tagged token (and the reverse)
- [ ] #2 #2 Unit tests: KeywordMatcher/n-gram search finds title 'Decatur St.' with keyword 'street' (and the reverse direction: keyword 'st' matching 'Street')
- [ ] #3 #3 Simulator live-verify on minix: FindSongByArtist 'twilight singers' + keyword 'street' resolves and plays 'Decatur St.'
- [ ] #4 #4 No regression: existing keyword-search tests unchanged; token count/purity for non-abbreviated titles unaffected
- [ ] #5 #5 /simplify + /code-review high pass before commit
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
IMPLEMENTED 2026-08-20 in TWO commits after live on-device follow-up:

COMMIT 1 (f52b990): abbreviation canonicalization map in KeywordMatcher.Tokenize (st/saint->street, rd->road, ave->avenue, pt->part, vol->volume; number/no excluded). Unit-verified.

COMMIT 2 (7aa891a): ON-DEVICE TESTING SHOWED THE FIX NEVER RAN ON THE LIVE PATHS. The user retried and it still failed (log 09:36: "search returned 0, artist=The Twilight Singers, keywords=street"). Audit found the shared failure pattern: a server-side substring pre-filter starves the keyword matcher of candidates before canonicalization can act. Three fixes:
1. FindSong artist-scoped: on empty NameContains pre-filter, retry with ArtistIds only (Limit 500) -> KeywordMatcher decides. THE live failure.
2. PlaySong WITH musician: on exact SearchTerm miss, fetch artist songs (Limit 500) and keyword-match. Covers the natural phrasing (the user's actual first attempt, 09:35).
3. PlaySong WITHOUT musician: on exact miss, consult the n-gram index (O(1)) + phonetic, before the cross-media artist fallback.

Also: shared BaseHandler.GetArtistSongsAsync (3 inline query blocks consolidated, JF-382 no-third-copy rule); review fixes applied (unbounded artist-songs fetch capped Limit 500 - aggregate "Various Artists" entries are the JF-358 budget shape); documented the deliberately-unfixed global DB fallback (cold path, self-heals, unfiltered retry would be unbounded catalog scan).

VERIFICATION: TDD (3 new tests, verified RED first; 2645 green, Release 0 warnings). /simplify + /code-review high findings all applied. LIVE-VERIFIED END-TO-END on minix post-commit: simulator PlaySong 'decatur street'+'twilight singers' plays 'Decatur St.' (metadata confirmed), and the no-musician variant via n-gram (log: "title fallback (n-gram index) matched 1 songs"). The AC#3 gap from commit 1 is now closed for the PlaySong paths (single-turn, simulable); the FindSong multi-turn flow remains beyond the simulator (no session persistence) but its artist-scoped fix is unit+code-path verified and shares the same matcher.

GH #19 comment updated pending release.
<!-- SECTION:FINAL_SUMMARY:END -->

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
