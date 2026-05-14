---
id: JF-120
title: Gapless/continuous playback with aggressive pre-fetch
status: Done
assignee: []
created_date: '2026-05-12 04:44'
updated_date: '2026-05-12 11:45'
labels:
  - enhancement
  - playback
  - audio
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Improve the PlaybackNearlyFinished handler to pre-fetch and queue the next stream URL before the current track ends, enabling gapless transitions between tracks. The current handler exists but could be more aggressive in pre-queuing.

Inspired by Music Assistant and JellyMusic which use PlaybackNearlyFinished to get the next stream URL for seamless track transitions.

Implementation: In `PlaybackNearlyFinished` handler, resolve the next queue item and generate its stream URL proactively. Enqueue via `PlayBehavior.ENQUEUE` with the expected `expectedPreviousToken`.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 PlaybackNearlyFinished pre-fetches next track stream URL
- [ ] #2 Gapless transition between tracks with no audible gap
- [ ] #3 Handles end-of-queue gracefully (no failed pre-fetch)
- [ ] #4 Works with shuffle and loop modes
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Enhanced PlaybackNearlyFinished handler with loop/shuffle support, optimized stream URL, fallback token resolution, and structured logging. Added 27 unit tests covering sequential/loop/shuffle/sleep-timer/radio modes. Build clean, 1046 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
- [ ] #2 dotnet build passes with 0 errors
- [ ] #3 dotnet test passes
- [ ] #4 No new compiler warnings introduced
- [ ] #5 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #6 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #7 NLU test fixtures updated if interaction model changed
- [ ] #8 E2E test added for new intent or handler logic
- [ ] #9 Locale response strings added to all 12 locales
<!-- DOD:END -->
