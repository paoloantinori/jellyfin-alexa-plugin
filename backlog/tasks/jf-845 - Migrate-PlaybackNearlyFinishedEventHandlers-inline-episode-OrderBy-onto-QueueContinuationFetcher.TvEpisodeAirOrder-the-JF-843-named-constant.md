---
id: JF-845
title: >-
  Migrate PlaybackNearlyFinishedEventHandler's inline episode OrderBy onto
  QueueContinuationFetcher.TvEpisodeAirOrder (the JF-843 named constant)
status: To Do
assignee: []
created_date: '2026-10-09 07:27'
labels:
  - cleanup
  - tech-debt
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-843 code-review altitude finding (2026-10-09). JF-843 introduced the named TV air-order constant QueueContinuationFetcher.TvEpisodeAirOrder (season-then-episode, (ParentIndexNumber, IndexNumber) ascending) and consumes it in TvNextUpService.GetEpisodeByAbsoluteNumberAsync. The coupling is user-visible: the absolute fallback ANNOUNCES the mapping it derives from this order, and the episode auto-advance derives succession from the same composite. PlaybackNearlyFinishedEventHandler.cs (~line 885, the candidatesQuery for unplayed-episode succession) still carries the value-identical INLINE OrderBy; JF-843 could not touch that file (surface boundary, another worker active). Migrate the inline OrderBy onto the shared constant so a future ordering tiebreak cannot make the announced absolute mapping diverge from next-episode succession within one session (divergence would be silent; no test asserts the composite equality across the two sites). The constant's doc comment names this task.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 PlaybackNearlyFinishedEventHandler's candidatesQuery OrderBy references QueueContinuationFetcher.TvEpisodeAirOrder, the inline composite initializer is gone
- [ ] #2 The candidatesQuery's own filters (IsPlayed=false, ParentIndexNumberNotEquals=0, IsVirtualItem=false, Limit) are unchanged and the auto-advance behavior is byte-identical (existing handler tests green, no behavior pin flips)
- [ ] #3 No new test failures on the full suite both TFMs
<!-- AC:END -->

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
