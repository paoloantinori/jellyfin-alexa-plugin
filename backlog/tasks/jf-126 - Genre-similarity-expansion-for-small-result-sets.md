---
id: JF-126
title: Genre similarity expansion for small result sets
status: Done
assignee: []
created_date: '2026-05-12 04:45'
updated_date: '2026-05-12 12:50'
labels:
  - enhancement
  - discovery
  - search
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When a genre search yields few results, automatically expand to include similar/related genres. This helps users with niche genres or small libraries get meaningful playback results.

Inspired by JellyMusic's response variants for "similar genres": `GENRE_PLAYING_SIMULAR`, `GENRE_SHUFFLE_SIMULAR`, `GENRE_QUEUED_SIMULAR`.

Implementation: In PlayByGenreIntent, if result count is below a threshold, query for related genres (could use Jellyfin's genre relationships, last.fm API, or a local mapping). Merge results and inform the user.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 When exact genre match yields few results, skill expands to include related/similar genres
- [x] #2 Genre similarity can be based on Jellyfin genre tags or a configurable mapping
- [x] #3 User is informed when similar genres are included ('playing rock and similar genres')
- [x] #4 Localized in all 12 locales
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
GenreSimilarityMap with 20 genre families. PlayByGenreIntentHandler expands to similar genres when exact match < 5 results. GenreExpanded string in all 12 locales. 18 new tests (15 map + 3 handler).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
- [ ] #2 dotnet build passes with 0 errors
- [ ] #3 dotnet test passes
- [ ] #4 No new compiler warnings introduced
- [ ] #5 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #6 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #7 NLU test fixtures updated if interaction model changed
- [ ] #8 E2E test added for new intent or handler logic
- [ ] #9 Locale response strings added to all 12 locales
<!-- DOD:END -->
