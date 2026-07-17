---
id: JF-164
title: >-
  Build in-memory artist index with event-driven refresh for fast fallback
  search
status: Done
assignee: []
created_date: '2026-05-17 06:59'
updated_date: '2026-05-17 07:44'
labels:
  - performance
  - enhancement
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Background

Performance testing (JF-163) showed that the current 4-tier DB query fallback chain for artist search is faster than a single-query + in-memory approach for libraries with 1000+ artists, because the full-table fetch costs 130-270ms. However, if the artist list were already in memory (pre-loaded), the in-memory matching would be ~1-7ms vs 5-200ms for DB queries.

## Scope

Build a background in-memory index of MusicArtist items that:
1. Loads all artists at plugin startup
2. Refreshes on library change events (`ItemAdded`, `ItemRemoved`, `LibraryChanged`)
3. Is queried by `PlayArtistSongsIntentHandler` (and potentially other artist-searching handlers) instead of DB queries
4. Runs the 4-tier fallback chain (ContainsThenFuzzy → PrefixFirstWord → PrefixFull → Contains) against the in-memory list

## Why artists only

- **Small dataset**: hundreds to low thousands of artists (fits in memory easily)
- **Most expensive path**: 6 handlers do artist search, `PlayArtistSongsIntentHandler` makes up to 4 sequential queries
- **Songs/albums are too large**: tens of thousands of items, frequent changes, DB-level filtering (SearchTerm, ArtistIds) already efficient

## Expected outcome

- Artist search latency: 1-7ms (down from 5-200ms)
- Zero DB connection pool pressure for artist lookups
- All 6 artist-searching handlers benefit from the shared index
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Artist index loads at plugin startup
- [x] #2 Index refreshes on library change events
- [x] #3 PlayArtistSongsIntentHandler uses in-memory index instead of DB queries
- [x] #4 Performance regression test: artist search < 10ms
- [x] #5 Unit tests for index lifecycle (load, refresh, empty library)
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Plan

1. **Explore** codebase: PlayArtistSongsIntentHandler, BaseHandler fuzzy matching, Jellyfin event system
2. **Design** ArtistIndex service: IArtistIndex interface, ArtistIndexService implementation
3. **Implement** ArtistIndexService with event-driven refresh
4. **Refactor** PlayArtistSongsIntentHandler to use in-memory index
5. **Performance test** measure before/after
6. **Simplify** code review
7. **E2E test** verify end-to-end
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
## Summary

Built an in-memory artist index (`ArtistIndexService`) that pre-loads all MusicArtist items at plugin startup and refreshes via debounced library change events (5s window). Replaced the 4-tier sequential DB query fallback chain in `PlayArtistSongsIntentHandler` with equivalent in-memory searches.

### Files Created
- `Alexa/IArtistIndex.cs` — Interface with `GetArtists(topParentIds)`, `IsReady`, `Count`
- `Alexa/ArtistIndexService.cs` — Implementation: IHostedService + IArtistIndex, event-driven refresh, volatile list swaps, pre-computed topParentId map
- `Tests/Unit/ArtistIndexServiceTests.cs` — 12 tests (load, filter, refresh, disposal, performance regression <10ms for 2005 artists)
- `Tests/Handler/PlayArtistSongsIntentHandlerTests.cs` — 8 tests (in-memory path, DB fallback, fuzzy match, edge cases)

### Files Modified
- `Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs` — Optional `IArtistIndex` injection, conditional in-memory vs DB search
- `EntryPoints/Registrator.cs` — DI registration (singleton + interface + hosted service sharing one instance)

### Performance
- Artist search latency: ~1-7ms in-memory vs 5-200ms DB queries (4 tiers)
- Performance regression test asserts <10ms for 2005 artists
- 1506 tests pass (1486 original + 20 new), build succeeds with 0 errors
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
