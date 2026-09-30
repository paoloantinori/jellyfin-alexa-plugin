---
id: JF-680
title: >-
  JF-680 - the in-lock own-live prewrite row is unpinned (episode + song): a
  miswired gate silently reverts concurrent lock-waiters to live-edge serve
status: To Do
assignee: []
created_date: '2026-09-30 05:18'
labels:
  - encode-gate
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-679 -
    Extract-the-episode-own-live-prewrite-serve-block-duplicated-verbatim-between-fast-path-and-in-lock-path.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn from the JF-679 orchestrator gate-marker round (finding 2; pre-existing coverage gap at baseline, surfaced by the extraction review).

THE GAP: the IN-LOCK own-live prewrite row has no direct pin on either the episode or the song path. The JF-677 in-lock twins (VideoAudioControllerTests ~1178-1400) all drive the own-dead ENDLIST row with no active encode (the JF-679 helper's gate returns null there), and the JF-675 own-ticks twins drive the FAST path only. So the row where a concurrent lock-waiter with a live own-ticks generation gets the PRE-WRITTEN listing (the JF-531/JF-536 mechanism's whole point for lock-waiters) is unpinned: a regression that drops or miswires ServeEpisodeWarmCacheAsync / TryServeOwnLiveVideoAudioPrewriteAsync at either in-lock site (e.g. passing wrong ticks so the gate never fires) compiles and keeps all ~4770 tests green, silently reverting concurrent lock-waiters to the live-edge serve; only a device-level live-edge symptom would surface it.

THE WORK: two pins (episode + song) driving the in-lock own-live row: hold the production per-(key,ticks) lock via _cache.LockItemAsync (the JF-677 ServeInLockWarmCacheAsync construction), start the endpoint with an OWN-LIVE generation marked under the same ticks (the seam family has the marking shapes), plant the prewrite listing, release the lock, and assert the served content IS the prewrite (its marker segment), not the live partial. Red proof: gate disabled or miswired ticks must flip the pin to the live-edge serve.

VERIFICATION: the two new pins + the full existing roster green both TFMs; no production change.
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
