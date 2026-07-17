---
id: JF-285
title: Add phonetic tolerance to song title search (FindSong + n-gram index)
status: Done
assignee:
  - claude
created_date: '2026-06-08 13:12'
updated_date: '2026-06-08 15:10'
labels:
  - enhancement
  - search
  - i18n
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/SongNgramIndexService.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/ArtistIndexService.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/DoubleMetaphone.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/FuzzyMatcher.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/FindSongIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Song title search (FindSongIntentHandler + SongNgramIndexService) has no phonetic tolerance — unlike artist search which uses Double Metaphone via ArtistIndexService + FuzzyMatcher. Non-native English speakers misspelling song titles (e.g., "rapsodi bohemian" for "Bohemian Rhapsody") get zero results.

Need to:
1. Add Double Metaphone phonetic encoding to SongNgramIndexService (same pattern as ArtistIndexService stores phonetic codes per artist). Store phonetic codes per token in the index.
2. Modify Search() to try phonetic matching when exact token match yields no results — encode the user's query tokens phonetically and match against pre-computed song token phonetic codes.
3. Ensure the DB fallback path in FindSongIntentHandler also benefits (it currently uses NameContains which is exact substring — consider adding a phonetic retry).
4. Add tests with misspelled titles across locales (Italian, German, French speakers misspelling English titles).
5. Performance: phonetic matching should only activate on exact-match miss, keeping the fast path unchanged.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Implementation Plan

### Architecture: Phonetic Token Index (pre-computed, O(1) lookup)

**Fast path (unchanged):** user keywords → bigram O(1) → KeywordMatcher.Score (100% coverage)
**Phonetic fallback (cold path only):** exact miss → phonetic encode keywords → `_phoneticTokenIndex` O(1) → KeywordMatcher.ScorePhonetic (50% coverage, 0.75 penalty)

### Files to modify:

1. **PluginConfiguration.cs** — Add `PhoneticSongSearchEnabled` flag (default: true, native speakers can disable)
2. **ISongNgramIndex.cs** — Add `SearchPhonetic()` to interface
3. **SongNgramIndexService.cs** — Add `_phoneticTokenIndex` dictionary, build at refresh, implement `SearchPhonetic()`
4. **KeywordMatcher.cs** — Add `ScorePhonetic()` static method (relaxed coverage, phonetic code matching, penalty multiplier)
5. **FindSongIntentHandler.cs** — Wire phonetic search: exact miss + flag enabled → call `SearchPhonetic()`

### Performance guarantees:
- Phonetic index pre-computed at refresh (same as bigram index)
- Lookup is O(1) dictionary access
- Only activates when exact search returns 0 results
- Double Metaphone encoding of user keywords is O(n) per token (~10 chars max)
- ScorePhonetic re-encodes title tokens of candidates only (small set, cold path)

### Test plan:
- Unit tests for `KeywordMatcher.ScorePhonetic` with misspelled titles
- Unit tests for `SongNgramIndexService.SearchPhonetic`
- Guard-clause tests for feature flag
- Edge cases: empty tokens, stop-words-only, single token queries
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
## Implementation Complete

### What was done:
Added Double Metaphone phonetic matching to song title search so non-native speakers can find songs with misspelled titles (e.g., "rapsodi" → "rhapsody", "fotograf" → "photograph").

### Files changed (6 files, +741/-13 lines):
1. **PluginConfiguration.cs** — `PhoneticSongSearchEnabled` feature flag (default: true, native speakers can disable)
2. **ISongNgramIndex.cs** — `SearchPhonetic()` interface method
3. **SongNgramIndexService.cs** — `_phoneticTokenIndex` dictionary pre-computed at refresh, `SearchPhonetic()` implementation, unified `AddToIndex()` helper
4. **KeywordMatcher.cs** — `ScorePhonetic()` with relaxed 50% keyword coverage, 0.75 penalty multiplier, reuses `FuzzyMatcher.PhoneticCodesMatch`
5. **FindSongIntentHandler.cs** — Phonetic fallback between exact n-gram miss and DB fallback, gated by feature flag
6. **PhoneticSongSearchTests.cs** — 24 new unit tests (cross-language misspellings, coverage thresholds, performance, edge cases)

### Performance:
- Phonetic index pre-computed at refresh (same as bigram index)
- O(1) phonetic code lookup at search time
- Only activates on exact-match miss (cold path)
- 2,000 songs phonetic search under 20ms

### Verification:
- Build: 0 errors, 0 warnings
- Tests: 2,301 pass (24 new phonetic + 2,277 existing)
- Deployed to minix: 12,745 songs, 3,230 phonetic codes indexed
- Config survived deploy (1 user preserved)

### Simplify pass applied:
- Eliminated `PhoneticCodesMatch` duplication → calls `FuzzyMatcher.PhoneticCodesMatch`
- Unified `AddToIndex` helper for bigram/single-token/phonetic index building
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
