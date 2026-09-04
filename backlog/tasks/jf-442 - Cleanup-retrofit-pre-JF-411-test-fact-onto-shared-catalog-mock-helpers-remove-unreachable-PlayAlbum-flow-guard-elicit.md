---
id: JF-442
title: >-
  Cleanup: retrofit pre-JF-411 test fact onto shared catalog mock helpers;
  remove unreachable PlayAlbum flow-guard elicit
status: In Progress
assignee: []
created_date: '2026-09-01 21:25'
updated_date: '2026-09-04 20:29'
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
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
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
- [x] #1 HandleAsync_MusicianOnly_PlaysArtistsAlbum uses SetupIndefiniteAlbumCatalog and GetPlayedTrackTokenAsync instead of its inline mock + directive extraction; the AlbumArtistIds resolution-query assertion is preserved
- [x] #2 The flow-guard elicit in PlayAlbumIntentHandler.HandleAsync is either removed with a proof of unreachability in the task notes, or kept with a comment justifying it as a defensive guard
- [x] #3 Full test suite passes with 0 failures
- [x] #4 BuildAlbumElicitResponse and BuildSongElicitResponse are consolidated onto one shared BaseHandler elicit builder (or the divergence is justified in comments)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-02: JF-422 code-review (high effort) landed three more items in this same family; fold them into this cleanup when it runs.

3. ELICIT-BUILDER TWIN: PlayAlbumIntentHandler.BuildAlbumElicitResponse is now a line-for-line twin of PlaySongIntentHandler.BuildSongElicitResponse (~line 451): same skeleton (ShouldEndSession=false, speech+reprompt, ElicitSlotDirective declaring ALL intent slots, ConversationalFlows.MarkOthersInactive), differing only in prompt key, slot name, intent name. Hoist a BaseHandler builder BuildDialogElicitResponse(promptKey, locale, slotToElicit, intentName, allSlotNames) serving both; FindSongIntentHandler.BuildElicitSlotResponse is a genuinely different shape (session attributes, 2-arg directive) and stays separate. Both doc comments already duplicate the same 2026-08-28 INVALID_RESPONSE lesson, so the next dialog-level fix must land twice today.

4. DIALOG-DELEGATION CATALOG MOCK: DialogDelegationTests.PlayAlbum_WithPartialSlots_ResolvesAlbumByArtist_NoDelegation hand-rolls a GetItemList dispatch duplicating SetupIndefiniteAlbumCatalog, with fidelity loss (returns the MusicAlbum for Audio-typed queries; stable ids were at least hoisted out of the callback in JF-422). Hoist SetupIndefiniteAlbumCatalog to the shared test helpers and keep only the Dialog.Delegate-absence assertion in that file; its scenario otherwise duplicates PlayAlbumIntentHandlerTests.HandleAsync_DialogInProgressWithMusician_PlaysArtistsAlbum_NoTitlePrompt, which is strictly stronger.

Note for item 2: the flow-guard unreachability proof in the JF-422 review matches the one already written here (past the entry elicit at least one slot is non-empty; empty album implies musician set, which returns NotFoundAlbumByArtist or resolves album via PickMostTracksRelease; only a null/whitespace MusicAlbum.Name reaches the guard).

2026-09-04 (executed, all four items done):

1. RETROFIT DONE. HandleAsync_MusicianOnly_PlaysArtistsAlbum now builds its catalog via the shared fixture mock (_fx.SetupIndefiniteAlbumCatalog + MakeRelease) and extracts the play through GetPlayedTrackTokenAsync; the AlbumArtistIds resolution-query assertion is preserved verbatim and a token-equality assert was added (matching the strictly stronger JF-422 sibling fact).

2. FLOW GUARD REMOVED, unreachability re-verified against the current code (JF-489 and JF-469 paths included):
   - The entry elicit (both slots empty) returns first; past it at least one slot is non-empty.
   - JF-489 musician calling-word block fires only when musician is non-empty AND album is empty. On HIT album is set to the stripped remainder, which TryStripLeadingAlbumCallingWord guarantees non-empty (it returns false for a bare calling word and rejects an empty trimmed remainder); on MISS musician becomes that same non-empty stripped value. Both outcomes leave a non-empty slot driving the resolution path.
   - With musician set, a zero-result artist search exits as NotFoundAlbumByArtist. A match feeds the JF-411 block, which returns on JF-471 acceptance refusal, JF-473 coincidental containment, or zero artist albums; otherwise album = resolvedAlbum.Name from PickMostTracksRelease over a non-empty candidate list.
   - The JF-469 album-slot stripped retry runs only inside the album-title branch (album already non-empty there) and never assigns album.
   - Conclusion: the guard was reachable only when a MusicAlbum row carries a null/whitespace Name, a state Jellyfin's scanner does not produce. The removal left three downstream uses needing the invariant (album.Length, two ResponseStrings format args, TryEntityFallbackAsync slotText); they carry album! against the title-guaranteed invariant, documented by the comment at the former guard position in PlayAlbumIntentHandler.HandleAsync.

3. ELICIT BUILDER HOISTED. The response skeleton was already shared (BaseHandler.BuildElicitSlotResponse); the new BaseHandler.BuildDialogElicitResponse(promptKey, locale, slotToElicit, intentName, allSlotNames) absorbs the prompt-key resolution so both handlers call it directly at their single call sites. BuildAlbumElicitResponse and BuildSongElicitResponse (and their duplicated 2026-08-28 INVALID_RESPONSE doc comments) are deleted; the handler-specific history (album: the on-device "quali ci sono" plain-Ask fallout; song: the JF-413 note plus the dialog.intents registration verified 2026-08-29) moved to brief call-site comments. FindSongIntentHandler's genuinely different variant is untouched.

4. CATALOG MOCK HOISTED. SetupIndefiniteAlbumCatalog is now a HandlerTestFixture method in Tests/Unit/TestHelpers.cs (same signature and doc comment); PlayAlbumIntentHandlerTests re-points all 15 call sites at _fx.SetupIndefiniteAlbumCatalog. DialogDelegationTests.PlayAlbum_WithPartialSlots_ResolvesAlbumByArtist_NoDelegation uses the shared mock and keeps only the Dialog.Delegate-absence assertion; the play/resolution outcome of that scenario stays pinned by the strictly stronger PlayAlbumIntentHandlerTests.HandleAsync_DialogInProgressWithMusician_PlaysArtistsAlbum_NoTitlePrompt.

Verification: dotnet build 0 warnings 0 errors; full suite 3242/3242 passed. Note: a CONCURRENT session's uncommitted JF-457/JF-491 work shares this checkout (ArtistSearch/LibraryFilter/PlayArtistSongs changes, +7 of its own new tests); its ArtistSearchTests.SearchAsync_DbTier1_RestrictedUser_DropsExcludedLibraryArtist_KeepsFolderlessOwn was mid-work and failing during this task's runs (it does not exist at HEAD and failed in isolation with none of this diff's code paths involved) and passed green by the final run. Net test count vs the 3235 HEAD baseline from THIS task: unchanged (4 tests moved from the deleted Handler twin file into the Unit class).
<!-- SECTION:NOTES:END -->
