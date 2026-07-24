---
id: JF-286
title: Add APL NowPlaying screen to carousel tap (HandleSelectItem)
status: Done
assignee: []
created_date: '2026-06-09 12:35'
updated_date: '2026-07-16 20:26'
labels:
  - apl
  - ux
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When a user taps an item on the APL carousel, `HandleSelectItem` only sends a bare `AudioPlayer.Play` directive via `BuildAudioPlayerResponse`. This means the Echo Show falls back to its minimal audio UI — no rich NowPlaying screen with album art, progress bar, or controls.

This is especially noticeable for audiobooks (Folder items resolved to children), but affects all carousel taps (Audio, Folder, Movie).

**Fix**: Add an `Alexa.Presentation.APL.RenderDocument` directive to the `HandleSelectItem` response, reusing the existing APL NowPlaying template that intent handlers already render (the one used by `PlayArtistSongsIntentHandler` etc.).

**Scope**: `AplUserEventHandler.HandleSelectItem` — after `BuildAudioPlayerResponse`, append the APL RenderDocument directive. Needs to handle the item image URL and metadata. Must respect the `NativeControlsForAudio` setting (which routes through VideoApp instead).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented in AplUserEventHandler.HandleSelectItem + BaseHandler.TryAttachNowPlayingDirective. Extracted shared helper to BaseHandler following TryAttachListDirective/TryAttachCarouselDirective pattern. 5 unit tests added covering: APL+Audio, APL+Folder, non-APL device, Movie (VideoApp path), carousel tap. All 34 tests pass.

Remaining: Next/Prev handlers should also call TryAttachNowPlayingDirective to update the NowPlaying screen (noted by code review but out of scope for this task).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented and present in the repo: TryAttachNowPlayingDirective extracted into BaseHandler.cs and wired into AplUserEventHandler.HandleSelectItem (RenderDocument on carousel tap), with 5 unit tests per the implementation note. Code shipped (committed untagged). The "Next/Prev handlers should also call TryAttachNowPlayingDirective" item was explicitly out of scope (separate follow-up). Closed during 2026-07-16 backlog reconciliation.
<!-- SECTION:FINAL_SUMMARY:END -->
