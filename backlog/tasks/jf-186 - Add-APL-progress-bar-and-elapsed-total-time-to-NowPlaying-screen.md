---
id: JF-186
title: Add APL progress bar and elapsed/total time to NowPlaying screen
status: Done
assignee: []
created_date: '2026-05-19 18:33'
updated_date: '2026-05-19 18:43'
labels:
  - apl
  - ux
  - playback
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add a self-advancing progress bar and elapsed/total time display to the existing NowPlaying APL template on Echo Show devices.

## Approach
Use `AlexaProgressBar` with `handleTick` tick event handlers (APL 1.4+) to create a client-side progress bar that advances every second. This is a documented Amazon APL feature — no third-party examples exist but the building blocks are all officially supported.

## Implementation
1. Add `AlexaProgressBar` to the NowPlaying APL template (AplHelper.BuildNowPlayingDirective or equivalent)
2. Set `totalValue` = track duration in ms from Jellyfin item metadata (`RunTimeTicks`)
3. Set initial `progressValue` = current offset in ms (from AudioPlayer context or session state)
4. Add `handleTick` with `minimumDelay: 1000` to increment `progressValue` by 1000 each second
5. Add `Text` components showing formatted elapsed/total time (mm:ss)
6. Add `handleTick` + `SendEvent` heartbeat every ~30s to keep the session alive (prevents APL document from being garbage collected during long playback)
7. Gate behind `AplVisualsEnabled` config flag (already exists)

## Key data needed
- Track duration: `BaseItem.RunTimeTicks` → convert to milliseconds
- Current offset: AudioPlayer context `OffsetInMilliseconds`, or session `PlayState.PositionTicks`

## Limitations (document in code comments)
- Progress bar is a client-side approximation, drifts ~1-3s from actual AudioPlayer position
- No data binding between APL and AudioPlayer — bar starts when document renders
- Not interactive — no seek/scrub (custom skills don't have AudioPlayer seek)
- Native scrubber UI (Spotify/Amazon Music style) is exclusive to Music/Radio interaction model
- APL document lifetime is limited (~30 min) — heartbeat SendEvent extends this

## Files to change
- `Apl/AplHelper.cs` — add progress bar to NowPlaying template, pass duration/offset in data sources
- `BaseHandler.cs` — ensure `BuildAudioPlayerResponse` passes duration metadata
- Handler that renders NowPlaying — pass `RunTimeTicks` and current offset to APL data
- Tests for new APL template structure
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 AlexaProgressBar rendered on NowPlaying screen when APL device plays audio
- [ ] #2 Elapsed time text updates every second via handleTick
- [ ] #3 Total duration text shows track length from Jellyfin metadata
- [ ] #4 Progress bar initializes to current offset (not 0) when resuming
- [ ] #5 handleTick stops advancing when progressValue reaches totalValue
- [ ] #6 Session kept alive via periodic SendEvent heartbeat during playback
- [ ] #7 Progress bar hidden on non-APL devices (graceful fallback)
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added AlexaProgressBar with handleTick self-advancing mechanism to NowPlaying APL template. Elapsed/total time displayed as formatted mm:ss text. Progress initialized from AudioPlayer offset, duration from Jellyfin RunTimeTicks. FormatTime helper handles h:mm:ss for long tracks. 15 new tests covering data sources, template structure, and time formatting. All 1717 tests passing.
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
