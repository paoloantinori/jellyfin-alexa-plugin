---
id: JF-576
title: >-
  Radio pool is a pure genre-membership random draw: GenreSimilarityMap is dead
  code on FindRadioTracksAsync (JF-126 residual)
status: To Do
assignee: []
created_date: '2026-09-16 11:01'
labels:
  - search-quality
  - radio
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Secondary finding from the JF-574 investigation (2026-09-16): the AutoPlay/radio pool built by RadioTrackSource.FindRadioTracksAsync is a PURE genre-membership random draw (Genres = seed.Genres, Limit=50, OrderBy Random, ShuffleAndCap 15/20). GenreSimilarityMap (shipped in JF-126 for small result sets) is DEAD CODE on this path: nothing consults it when the seed genre yields few tracks. Consequence: on the Norah Jones incident shape the radio replacement drew 15 arbitrary Jazz tracks with no similarity ranking, compounding the false-exhaustion bug. Scope: (1) verify with a reading pass that GenreSimilarityMap truly has zero callers on the FindRadioTracksAsync path; (2) either wire similarity ranking into the pool build (seed-artist/genre adjacency ordering before ShuffleAndCap) or delete the dead map and close JF-126's residual claim honestly; (3) small-result-set behavior is the original JF-126 motivation, so preserve or supersede that contract explicitly. File under area B (search/catalog quality).
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
