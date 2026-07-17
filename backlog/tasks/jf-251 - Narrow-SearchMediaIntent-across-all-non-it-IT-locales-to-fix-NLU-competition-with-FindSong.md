---
id: JF-251
title: >-
  Narrow SearchMediaIntent across all non-it-IT locales to fix NLU competition
  with FindSong
status: Done
assignee: []
created_date: '2026-06-04 11:00'
updated_date: '2026-06-04 11:41'
labels:
  - bug
  - NLU
  - i18n
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
SearchMediaIntent's greedy single-word+slot patterns ("Find {query}", "Search {query}", "Cerca {query}", "Trova {query}") capture utterances that should route to FindSongIntent/FindSongByArtistIntent. Fixed for it-IT in commit 07ae950 by replacing with media-type-qualified patterns. Need same treatment for en-US, en-GB, en-AU, en-CA, en-IN, de-DE, fr-FR, fr-CA, es-ES, es-MX, es-US, pt-BR, ja-JP, ar-SA, nl-NL, hi-IN.

**en-US patterns to narrow:**
- "Search for {query}" → "Search for a movie {query}", "Search for content {query}"
- "Find {query}" → "Find a movie {query}", "Find content {query}"
- "Search {query}" → "Search movies {query}"
- "Find me {query}" → "Find me a movie {query}"

**Pattern**: Replace ultra-short 1-2 word + slot patterns with media-type-qualified carrier phrases (movie, video, series, audiobook, content). Keep the intent handling all media types — just narrow the NLU patterns.

**Acceptance**: en-US FindSong NLU fixtures pass ("find a song by police" → FindSongByArtistIntent, "find a song called breath" → FindSongIntent).
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Narrowed SearchMediaIntent across all 16 non-it-IT locales. Replaced greedy 1-2 word + slot patterns with media-type-qualified carriers (movie, video, series, audiobook, content) in each locale's language. Kept library-specific patterns unchanged. All 2205 tests pass, model validation passes (104 pre-existing warnings, 0 new). Committed as 8030a17, pushed to main.
<!-- SECTION:FINAL_SUMMARY:END -->
