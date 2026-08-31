---
id: JF-424
title: >-
  NextTrackPrecomputeCache.TryGet consumes the FRESH entry on token mismatch
  (late NearlyFinished discards current-track precompute)
status: To Do
assignee: []
created_date: '2026-08-31 15:02'
labels:
  - code-review
  - playback
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Playback/NextTrackPrecomputeCache.cs:74'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review finding (2026-08-31, high effort, PLAUSIBLE: code shape confirmed, trigger rests on Amazon's documented multi-fire NearlyFinished behavior). NextTrackPrecomputeCache.cs:74 (TryGet).

DEFECT: TryGet consumes the entry (TryRemove) unconditionally, including on token mismatch. Sequence: NearlyFinished(A) consumes entry(A->B); PlaybackStarted(B) stores entry(B->C); Amazon re-sends a late/duplicate NearlyFinished(A) (documented to fire more than once and on stalls); TryRemove deletes the fresh (B->C) entry and the token mismatch discards it; the real NearlyFinished(B) then finds no cache and performs full library + stream-URL resolution. That is the 11-20s server-stall path JF-390/JF-410 exist to avoid, risking a blown ~8s Alexa window and an audible gap between tracks.

NOTE: the JF-409 fix only needed the token-mismatch REJECTION; the consumption on mismatch is incidental damage.

FIX SHAPE: only remove the entry when the token matches (peek-then-remove, or remove with the token as a key/comparison). If cache size/staleness is a concern, handle expiry separately from the mismatch path.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A token-mISMATCH TryGet no longer removes the stored entry: the fresh entry for the current track survives a late/duplicate NearlyFinished for the previous track
- [ ] #2 Token-match consumption is unchanged (the normal A->B precompute flow still works, existing tests green)
- [ ] #3 Unit test: store entry(B->C), TryGet(token A) fails AND leaves the entry, then TryGet(token B) succeeds
- [ ] #4 Behavior on genuinely stale entries (old track, no current playback) documented or handled explicitly
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
