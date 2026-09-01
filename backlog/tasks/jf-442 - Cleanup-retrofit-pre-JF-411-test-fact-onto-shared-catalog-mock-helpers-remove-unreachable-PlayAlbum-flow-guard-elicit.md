---
id: JF-442
title: >-
  Cleanup: retrofit pre-JF-411 test fact onto shared catalog mock helpers;
  remove unreachable PlayAlbum flow-guard elicit
status: To Do
assignee: []
created_date: '2026-09-01 21:25'
updated_date: '2026-09-01 21:26'
labels:
  - code-quality
  - tech-debt
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/PlayAlbumIntentHandlerTests.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two minor cleanups surfaced by the JF-427 /simplify pass (2026-09-01), both in PlayAlbumIntentHandler / its test file, both deliberately NOT done inside JF-427 to keep its diff minimal (drive-by refactor of untouched code).

1. TEST HELPER RETROFIT: PlayAlbumIntentHandlerTests.cs now has SetupIndefiniteAlbumCatalog (query-recording dispatch mock) + GetPlayedTrackTokenAsync (AudioPlayer token extraction). The PRE-EXISTING JF-411 fact HandleAsync_MusicianOnly_PlaysArtistsAlbum (~line 494) still hand-rolls the same two patterns inline. Retrofit that one fact onto the shared helpers; its AlbumArtistIds assertion stays.

2. DEAD FLOW-GUARD ELICIT: PlayAlbumIntentHandler.HandleAsync has a flow-guard `if (string.IsNullOrWhiteSpace(album)) return BuildSlotElicitResponse(Album, ...)` right after the JF-411 block (the "Flow guard: past this point an album title is guaranteed" comment). With the earlier guards (no musician -> elicit musician at the top; dialogInProgress -> elicit album) plus the JF-411 block always leaving `album` non-empty when a musician exists, this branch is unreachable except for a null album Name. Pre-dates JF-427. Verify unreachability (including the JF-427 carry path and the dialogInProgress cases), then remove or guard it with a justification comment if it must stay defensive.

Both changes are behavior-neutral; full PlayAlbum test suite must stay green.
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
- [ ] #10 /code-review high passed (no blocking findings remaining)
- [ ] #11 Findings applied or tracked
<!-- DOD:END -->



## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 HandleAsync_MusicianOnly_PlaysArtistsAlbum uses SetupIndefiniteAlbumCatalog and GetPlayedTrackTokenAsync instead of its inline mock + directive extraction; the AlbumArtistIds resolution-query assertion is preserved
- [ ] #2 The flow-guard elicit in PlayAlbumIntentHandler.HandleAsync is either removed with a proof of unreachability in the task notes, or kept with a comment justifying it as a defensive guard
- [ ] #3 Full test suite passes with 0 failures
<!-- AC:END -->
