---
id: JF-120
title: Gapless/continuous playback with aggressive pre-fetch
status: To Do
assignee: []
created_date: '2026-05-12 04:44'
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

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
