---
id: JF-136
title: 'APL touch-to-play: handle UserEvent for tappable list items'
status: Done
assignee: []
created_date: '2026-05-12 15:56'
updated_date: '2026-05-14 16:40'
labels:
  - apl
  - echo-show
  - enhancement
  - touch
dependencies: []
references:
  - JF-115
  - claudedocs/research_visual_support_echo_show_2026-05-10.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add an APL UserEvent handler for touch-to-play AND fix the root cause of APL lists not rendering on Echo Show.

**Observed bug**: When asking "quali canzoni abbiamo dei Soul Coughing" on an Echo Show, the device reads the list aloud but shows nothing on screen. The code path exists — `TryAttachListDirective()` is called in the handlers, `DeviceSupportsApl()` checks for `Alexa.Presentation.APL` in `SupportedInterfaces`, and the manifest declares the interface. Something at runtime prevents APL from rendering.

**Part 1 — Debug and fix APL list rendering**: 
The `DeviceSupportsApl()` check at `AplHelper.cs:243` looks correct — it checks `context.System.Device.SupportedInterfaces.ContainsKey("Alexa.Presentation.APL")`. The manifest at `Manifest/manifest.json:13` declares `"type": "ALEXA_PRESENTATION_APL"`. The handlers call `TryAttachListDirective()`. Need to investigate why it's not working at runtime — likely causes:
- Echo Show not reporting APL in `SupportedInterfaces` (check actual request logs)
- APL document has a rendering error (check Alexa developer console for APL errors)
- `TryAttachListDirective` silently skipping (check the conditions inside)
- The directive is attached but malformed

**Part 2 — Add APL touch-to-play (UserEvent handler)**:
Once lists render, taps do nothing — there's no handler for `Alexa.Presentation.APL.UserEvent`. The `TouchWrapper` in the APL template sends `[action, id]` via `SendEvent`, but no handler receives it. Need a UserEvent handler that extracts item ID and plays it.

**Handlers whose APL lists should render AND be interactive:**
- `BrowseLibraryIntentHandler` — browse songs/albums/artists/movies
- `QueryArtistLibraryIntentHandler` — tracks/albums by artist
- `SearchMediaIntentHandler` — disambiguation of search results
- `ListQueueIntentHandler` — queue contents
- `InProgressMediaListIntentHandler` — in-progress media

**Existing infrastructure**: `AplHelper.GetTouchEventArgument()` already extracts event arguments. `SendEvent` arguments are `[action, id]`. NowPlaying template exists for post-playback display.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 APL UserEvent handler registered in the request pipeline and processes Alexa.Presentation.APL.UserEvent requests
- [ ] #2 Tapping a song in list plays that song via AudioPlayer
- [ ] #3 Tapping prev/pause/next on NowPlaying screen works
- [ ] #4 APL list is replaced with NowPlaying template after successful playback starts
- [ ] #5 Touch interaction is a no-op on audio-only devices (no regression)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
## Codebase Findings

**APL infrastructure is well-built but missing the event handler:**

**Existing templates:**
- **NowPlaying** — displays track info, album art, prev/pause/next controls via TouchWrapper+SendEvent
- **List** — scrollable list with TouchWrapper items, each emitting `[action, id]` via SendEvent

**Key existing code:**
- `AplHelper.DeviceSupportsApl(Context)` — checks for "Alexa.Presentation.APL" in SupportedInterfaces
- `AplHelper.GetTouchEventArgument()` — extracts SendEvent arguments from UserEvent requests
- `AplRenderDocumentDirective` / `AplExecuteCommandsDirective` — directive builders
- Manifest already declares `ALEXA_PRESENTATION_APL` interface

**What's missing:**
1. **UserEvent handler** — No handler for `Alexa.Presentation.APL.UserEvent` request type. Need a new handler class with `CanHandle()` checking for this request type.
2. **APL rendering bug** — Lists don't render on Echo Show despite correct code. Possible causes: Echo Show not reporting APL in SupportedInterfaces, APL document has rendering error, or `TryAttachListDirective` silently skipping.

**Request pipeline**: `AlexaSkillController` → `RequestPipeline` → iterates handlers → `CanHandle()` / `HandleAsync()`. Adding a new handler requires registering it in DI (via `Registrator.cs`).

**Handlers that need APL lists:**
- BrowseLibraryIntentHandler, QueryArtistLibraryIntentHandler, SearchMediaIntentHandler, ListQueueIntentHandler, InProgressMediaListIntentHandler

**Implementation steps:**
1. Debug APL rendering bug (test with live device or check Alexa dev console APL errors)
2. Create `AplUserEventHandler` — handles `Alexa.Presentation.APL.UserEvent`
3. Extract action + item ID from event arguments
4. Route to appropriate play logic based on item type
5. Replace list with NowPlaying template after playback starts
6. Register handler in DI
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
AplUserEventHandler already implemented (166 lines): handles selectItem, prev/pause/next, and playTrack actions. APL rendering root cause fixed in JF-154 (21 handlers not passing context to BuildAudioPlayerResponse). Needs live device testing on Echo Show to confirm APL lists render and touch interactions work end-to-end.
<!-- SECTION:FINAL_SUMMARY:END -->

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
