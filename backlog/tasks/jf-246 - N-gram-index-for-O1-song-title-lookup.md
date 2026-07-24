---
id: JF-246
title: N-gram index for O(1) song title lookup
status: Done
assignee: []
created_date: '2026-06-03 18:12'
updated_date: '2026-06-08 12:28'
labels:
  - enhancement
  - search
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Build an in-memory n-gram index (similar to ArtistIndexService) that maps every 2-3 word combination from song titles back to songs. This enables O(1) lookup speed for partial title matching. Should be an optional search strategy that users can enable via config.

**Trade-offs:** Fast lookup but large memory footprint for big libraries. Complex to keep in sync. Consider this after Approach A (artist-scoped keyword search) is validated.

**Depends on:** jf-172 (artist-scoped keyword search - Approach A) being implemented first to validate the UX pattern.
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Updated dependency: original reference to 'jf-172 (artist-scoped keyword search)' was stale — JF-172 was about duplicate utterances. The keyword search work was done via JF-245 (KeywordMatcher) + JF-248 (FindSongIntentHandler), both Done. Dependency is satisfied.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented SongNgramIndexService — an in-memory bigram index for O(1) song title lookup. Builds at startup from all Audio items, refreshes on library changes with 5s debounce. FindSongIntentHandler uses it as primary search (DB fallback when unavailable). Artist-scoped searches keep existing DB flow. 13 unit tests pass, 43 existing FindSong tests unaffected. Commit: 240c610.
<!-- SECTION:FINAL_SUMMARY:END -->
