---
id: JF-478
title: >-
  PlayAlbum title path auto-plays interior-containment album matches (dark side
  of the moon -> 'O' by Damien Rice); JF-408 gate missing on this path
status: In Progress
assignee: []
created_date: '2026-09-04 09:57'
updated_date: '2026-09-04 10:00'
labels: []
dependencies: []
references:
  - Device corr=80bb4642 (2026-09-04)
  - 'JF-408 (interior containment, cascade-side)'
  - JF-471 (the musician-path gate; different path)
  - PlaySongAlbumFallbackTests.PlaySong_InteriorContainmentAlbum_NotSubstituted
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-04 batch re-test (corr=80bb4642): 'riproduci album dark side of the moon' arrived with the album slot FILLED (real ASR fills it, unlike profile-nlu where the musician slot steals it: the two probe surfaces genuinely differ). PlayAlbum's album-title search missed, the fuzzy fallback matched Damien Rice's single-letter album 'O' at containment score 90, and the skill auto-played it: 'Ho trovato l'album O' + Damien Rice tracks.

This is EXACTLY the JF-408 interior-containment class (a coincidental substring containment reaching the 90 bar must not substitute): that rejection exists in TryAlbumFallbackAsync (the PlaySong cascade, pinned by PlaySong_InteriorContainmentAlbum_NotSubstituted) but NOT on PlayAlbum's primary album-title path (the HandleFuzzyMiss auto-play at >= 90 or the equivalent acceptance point). The JF-471 gate protects only the album-by-artist (musician) path.

Fix: extend the JF-408 interior-containment rejection (shared helper IsInteriorContainment in BaseHandler, reused by the cascade) to PlayAlbum's album-title acceptance point. Mirror the existing cascade test at the PlayAlbum level: album 'O' in the library + query 'dark side of the moon' -> clean NotFoundAlbumByName (or the CrossMediaArtistSuggestion offer per JF-363 if the artist band applies), never auto-play. The legit single-word containment classes the JF-408 research established must keep working (verify against the cascade's test set).
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
