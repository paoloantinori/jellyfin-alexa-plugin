---
id: JF-577
title: >-
  Hoist JF-574 queue rehydration to a shared guard and adopt it in the other
  wiped-queue consumers (Next/Previous/ListQueue/AddToQueue/PlaybackStarted
  precompute)
status: To Do
assignee: []
created_date: '2026-09-16 11:18'
labels:
  - reliability
  - refactor
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-574 /simplify altitude pass (2026-09-16): TryRehydrateSessionQueueFromDevice in PlaybackNearlyFinishedEventHandler repairs the restart-wiped session queue at the depth of ONE consumer, but the same wiped session.NowPlayingQueue state is mis-read by other consumers that all have access to DeviceQueueManager: NextIntentHandler (~line 78) and PreviousIntentHandler (~line 78) speak "no more tracks" while music still plays; ListQueueIntentHandler (~line 66) speaks an empty queue; AddToQueueIntentHandler (~line 189) replaces the surviving queue's membership basis with a one-item list; PlaybackStartedEventHandler's precompute path reads the session queue too. Proposed shape: hoist the guarded rehydration to the shared layer next to the mirror primitive (a SessionQueue.EnsureRehydrated(session, context, queueManager) or static on ProgressReporter beside MirrorQueueToSession), carrying the two-leg coherence guard (empty session queue AND persisted queue contains the codec-parsed playing token) and the rationale doc; NearlyFinished's call site collapses to one line and the other consumers adopt incrementally. Adopting rehydration in Next/Previous/ListQueue CHANGES behavior after a mid-playback restart (that is the point), so each adopter needs its own characterization + test. Scope: hoist + NearlyFinished re-route (behavior-neutral, lock with existing PlaybackRestartRehydrationTests), then per-consumer adoption with tests.
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
