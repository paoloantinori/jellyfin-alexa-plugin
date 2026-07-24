---
id: JF-157
title: Fix Alexa pause/resume to preserve playback state for reliable resume
status: Done
assignee: []
created_date: '2026-05-16 07:32'
updated_date: '2026-05-16 08:57'
labels:
  - bug
  - playback
  - audiobooks
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When a user says "pause", the skill sends `AudioPlayerStop()` which may clear the device's playback context. When the user later says "resume", the `ResumeIntentHandler` needs the item_id + offset to rebuild the stream URL, but this info may be lost.

Current state:
- `PauseIntentHandler` sends `ResponseBuilder.AudioPlayerStop()` — standard but destructive to playback context
- `ResumeIntentHandler` tries to get offset from `context.AudioPlayer.OffsetInMilliseconds` (may be gone after stop) or falls back to `session.PlayState.PositionTicks`
- `DeviceQueueManager` persists queue state to disk — this survives restarts
- Jellyfin session tracks `FullNowPlayingItem` — potential source for item_id

The fix likely involves:
1. Verify whether `AudioPlayerStop()` actually clears the offset on device (test with real device/simulator)
2. Ensure the skill persists the currently-playing item_id server-side (DeviceQueueManager or Jellyfin session)
3. Update `ResumeIntentHandler` to reliably reconstruct the AudioPlayerPlay directive even when Alexa context is empty
4. Consider using `ClearDirective` behavior or alternative stop mechanisms that preserve state
5. Test resume works for: songs, audiobooks (long offset), and podcasts

This is especially important for audiobooks where losing your position means minutes of seeking.
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
Fixed pause/resume state preservation by extending DeviceQueue with CurrentPositionTicks and CurrentItemId. PlaybackStoppedEventHandler saves position on stop, PlaybackNearlyFinishedEventHandler keeps it fresh during playback. ResumeIntentHandler uses three-tier fallback: Alexa context → Jellyfin session → DeviceQueue. Added 13 tests, all 1465 passing.
<!-- SECTION:FINAL_SUMMARY:END -->
