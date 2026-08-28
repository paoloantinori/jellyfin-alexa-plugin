---
id: JF-409
title: >-
  NextTrackPrecomputeCache ignores currentTrackToken (doc-vs-code): stale entry
  re-enqueues the currently playing track
status: In Progress
assignee:
  - zai
created_date: '2026-08-28 15:37'
updated_date: '2026-08-28 16:46'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live incident 2026-08-28 16:23:35: during album playback, PlaybackNearlyFinished served a stale precompute entry whose NextItemId equaled the CURRENT track (15714fe0 "Older Chests"), enqueuing the same track on itself (MoveTo "moved from index 4 to 4", expectedPreviousToken == token). User heard the same song twice (16:18:58 and 16:23:44 plays of the same item).

Root cause: NextTrackPrecomputeCache.Store/TryGet take a currentTrackToken parameter but BOTH ignore it (doc comment claims "Keyed by (deviceId, currentTrackToken)... valid only if the current track token matches"; the ConcurrentDictionary key is deviceId only, and TryGet never compares tokens). Chain: (1) entry stored at PlaybackStarted of track N-1 with next=trackN; (2) NearlyFinished of track N-1 consumed it but nothing removes it (no call site for Invalidate exists at all); (3) PlaybackStarted of trackN early-returns from precompute because trackN is the last item of the not-yet-extended progressive queue, so the stale entry survives; (4) NearlyFinished of trackN TryGet-hits the stale entry and enqueues trackN again. Self-heals only via the 15-min TTL.

Deployed in 0.12.0.0 (JF-390, commit 91c0bf2). Tests exist in Jellyfin.Plugin.AlexaSkill.Tests/Handler/PreEnqueueOnStartTests.cs.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Unit test: Store(device, tokenA, next=X); TryGet(device, tokenB) must MISS even within TTL
- [x] #2 Unit test: Store(device, tokenA, next=X); TryGet(device, tokenA) must HIT (regression)
- [x] #3 The duplicate-enqueue scenario is covered: when the current track is the last item of the not-yet-extended progressive queue (precompute early-returns), NearlyFinished must NOT serve a stale entry from a previous transition
- [x] #4 Consume-on-read (TryGet removes the entry) OR equivalent invalidation so one stored entry can never be served twice
- [x] #5 PreEnqueueOnStartTests.cs and the full suite pass via dotnet test (no --no-build)
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. TDD: failing tests first in the precompute test file — (a) Store(device, tokenA, next) then TryGet(device, tokenB) must MISS within TTL; (b) TryGet(device, tokenA) must HIT (regression); (c) second TryGet with same token must MISS (consume-on-read).
2. Fix NextTrackPrecomputeCache: dictionary key becomes deviceId + separator + currentTokenToken (doc contract finally implemented); TryGet consumes the entry on hit (one stored entry can never be served twice). Keep TTL and Invalidate unchanged.
3. Run full dotnet test (no --no-build, NuGet cache redirected to /tmp).
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented: cache key is now deviceId|currentTrackToken (the documented contract, finally real); TryGet consumes the entry on hit (single-shot); Invalidate clears all device-prefixed keys.

TDD followed: 4 new tests failed on the old code exactly per the incident (different-token hit, no consumption, stale entry served at handler level), then green after the fix.

Full suite: 2725 passed, 0 failed (dotnet test without --no-build).

NOTE CORRECTION (code-review git-history finding): an earlier note here describes the composite key design ('deviceId|currentTrackToken', 'Invalidate clears all device-prefixed keys'). That was REPLACED during the /simplify pass before commit. The SHIPPED design: dictionary keyed by deviceId ALONE, the current-track token stored INSIDE the entry and validated on read (mismatch = miss + consume), Store replaces the single per-device entry (bounded), Invalidate is a plain per-device TryRemove. The entry-population and separator-duplication problems of the composite key are why it was redesigned; do not restore the composite key.
<!-- SECTION:NOTES:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
