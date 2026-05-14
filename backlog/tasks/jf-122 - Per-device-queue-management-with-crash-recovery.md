---
id: JF-122
title: Per-device queue management with crash recovery
status: Done
assignee: []
created_date: '2026-05-12 04:45'
updated_date: '2026-05-12 12:50'
labels:
  - enhancement
  - playback
  - reliability
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Maintain independent playback queues per Echo device, persisted so they survive plugin restarts. Currently our queues are session-scoped and lost on restart.

Inspired by JellyMusic's per-device queue system: each device gets its own queue stored in JSON files for crash recovery. JellyMusic moved away from SQLite to flat JSON for simplicity.

Implementation: Track device ID (from Alexa request context) and maintain per-device queue state. Persist to a file or database. On PlaybackNearlyFinished, look up the device's queue rather than a global one.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Each Echo device maintains its own independent playback queue
- [ ] #2 Queue state persisted to allow recovery after plugin restart
- [ ] #3 Multiple devices in same household don't interfere with each other's queues
- [ ] #4 Queue state includes: track list, current position, shuffle/loop state
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DeviceQueueManager singleton with ConcurrentDictionary, JSON file persistence, debounced writes, crash recovery on startup. 25 unit tests. Injected as optional dependency in all queue-aware handlers.
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
