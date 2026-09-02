---
id: JF-442
title: >-
  Cleanup: retrofit pre-JF-411 test fact onto shared catalog mock helpers;
  remove unreachable PlayAlbum flow-guard elicit
status: To Do
assignee: []
created_date: '2026-09-01 21:25'
updated_date: '2026-09-01 23:28'
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
- [ ] #4 BuildAlbumElicitResponse and BuildSongElicitResponse are consolidated onto one shared BaseHandler elicit builder (or the divergence is justified in comments)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-02: JF-422 code-review (high effort) landed three more items in this same family; fold them into this cleanup when it runs.

3. ELICIT-BUILDER TWIN: PlayAlbumIntentHandler.BuildAlbumElicitResponse is now a line-for-line twin of PlaySongIntentHandler.BuildSongElicitResponse (~line 451): same skeleton (ShouldEndSession=false, speech+reprompt, ElicitSlotDirective declaring ALL intent slots, ConversationalFlows.MarkOthersInactive), differing only in prompt key, slot name, intent name. Hoist a BaseHandler builder BuildDialogElicitResponse(promptKey, locale, slotToElicit, intentName, allSlotNames) serving both; FindSongIntentHandler.BuildElicitSlotResponse is a genuinely different shape (session attributes, 2-arg directive) and stays separate. Both doc comments already duplicate the same 2026-08-28 INVALID_RESPONSE lesson, so the next dialog-level fix must land twice today.

4. DIALOG-DELEGATION CATALOG MOCK: DialogDelegationTests.PlayAlbum_WithPartialSlots_ResolvesAlbumByArtist_NoDelegation hand-rolls a GetItemList dispatch duplicating SetupIndefiniteAlbumCatalog, with fidelity loss (returns the MusicAlbum for Audio-typed queries; stable ids were at least hoisted out of the callback in JF-422). Hoist SetupIndefiniteAlbumCatalog to the shared test helpers and keep only the Dialog.Delegate-absence assertion in that file; its scenario otherwise duplicates PlayAlbumIntentHandlerTests.HandleAsync_DialogInProgressWithMusician_PlaysArtistsAlbum_NoTitlePrompt, which is strictly stronger.

Note for item 2: the flow-guard unreachability proof in the JF-422 review matches the one already written here (past the entry elicit at least one slot is non-empty; empty album implies musician set, which returns NotFoundAlbumByArtist or resolves album via PickMostTracksRelease; only a null/whitespace MusicAlbum.Name reaches the guard).
<!-- SECTION:NOTES:END -->
