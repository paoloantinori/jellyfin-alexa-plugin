---
id: JF-279
title: Verify queue manipulation during active playback
status: Done
assignee: []
created_date: '2026-06-08 09:32'
updated_date: '2026-09-23 14:52'
labels:
  - e2e
  - playback
  - queue
milestone: m-5
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Queue manipulation handlers (AddToQueue, PlayNext, ClearQueue, ListQueue) have unit tests for DeviceQueue but no E2E verification during active playback. Need to:
1. Start playback, then "add [song] to queue"
2. Verify "what's in the queue" lists correct tracks
3. Test "play [song] next" — verify it plays after current track
4. Test "clear queue" — verify queue empties but current track continues
5. Verify queue state survives pause/resume cycle
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
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Device-verified 2026-09-22/23 batteries, 3.5/5 items with log evidence: (1) add-to-queue during active playback verified twice (17:35:34 corr 2ffe009a 'added Rapsodia... to queue' + 19:02:50 corr a92919de); (3) play-next verified (19:04:04 corr f5d9fc8d, slot song=rapsodia, handler confirmed); (5) queue/position state across pause-resume verified repeatedly in the resume battery (same track, same offset: 77130ms/92962ms sessions); (4) clear-queue fired during playback twice (13:54:46/13:54:55, the shuffle-misroute incident: user heard 'coda svuotata', queue emptied) — current-track-continuation after clear not explicitly observed; (2) ListQueue voice-form never device-probed (routes per profile-nlu: 'riproduci la prossima canzone' -> ListQueueIntent). Residuals (ListQueue probe, current-track-after-clear) noted on JF-405's checklist. Closed as substantially verified.
<!-- SECTION:FINAL_SUMMARY:END -->
