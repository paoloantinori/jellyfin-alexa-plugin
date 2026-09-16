---
id: JF-574
title: >-
  Restart wipes the session queue the NearlyFinished resolver reads: false
  queue-exhaustion triggers AutoPlay radio over a surviving persisted device
  queue (Norah Jones incident)
status: To Do
assignee: []
created_date: '2026-09-16 05:53'
labels:
  - bug
  - playback
  - queue
  - restart-recovery
  - autoplay
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live incident 2026-09-16 07:28 (log-verified): a DLL hot-swap restart at 07:25:44 interrupted a PlayArtistSongs queue 1 minute after it was built (5-item first page, Norah Jones). At the first track's PlaybackNearlyFinished the resolver returned next item=null DESPITE the persisted device queue containing 4 more queued tracks (queue file verified: itemIds 5x Norah Jones, currentIndex=0). Root cause class: the NearlyFinished resolver reads the SERVER SESSION's in-memory NowPlayingQueue, which a restart wipes; the DeviceQueueManager file queue (the crash-recovery mechanism built for exactly this class, JF crash-recovery SetQueue) survives but is NOT consulted as a fallback when the session queue is absent. The false exhaustion then triggered PostPlay AutoPlay (user's per-user setting), which replaced the artist queue with 15 ListenBrainz-pool radio tracks from other artists. FIX DIRECTION: when PlaybackNearlyFinished/PlaybackStopped resolution finds session.NowPlayingQueue empty/null but the persisted device queue for the device exists and contains the current token with remaining items, rehydrate the session queue from the device queue (the crash-recovery path) before declaring exhaustion. Also evaluate: the AutoPopulate trigger should perhaps distinguish 'true queue exhaustion' from 'session state lost across restart'. SECONDARY (separate quality matter, same incident): the ListenBrainz 'similar' pool for Norah Jones returned White Stripes/DMB/Placebo/Cranberries - pool quality bounded by library, verify the similarity provider actually contributed vs a popularity fallback firing.
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
