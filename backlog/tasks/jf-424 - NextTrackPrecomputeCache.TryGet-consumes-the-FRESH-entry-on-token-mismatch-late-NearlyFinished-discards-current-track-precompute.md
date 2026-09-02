---
id: JF-424
title: >-
  NextTrackPrecomputeCache.TryGet consumes the FRESH entry on token mismatch
  (late NearlyFinished discards current-track precompute)
status: Done
assignee:
  - zai
created_date: '2026-08-31 15:02'
updated_date: '2026-09-01 22:44'
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
- [x] #1 A token-mISMATCH TryGet no longer removes the stored entry: the fresh entry for the current track survives a late/duplicate NearlyFinished for the previous track
- [x] #2 Token-match consumption is unchanged (the normal A->B precompute flow still works, existing tests green)
- [x] #3 Unit test: store entry(B->C), TryGet(token A) fails AND leaves the entry, then TryGet(token B) succeeds
- [x] #4 Behavior on genuinely stale entries (old track, no current playback) documented or handled explicitly
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-424: a late/duplicate NearlyFinished can no longer destroy the fresh precompute entry for the track now playing.

WHAT CHANGED (commit 0e509d1)
- NextTrackPrecomputeCache.TryGet: peek (TryGetValue) -> TTL check FIRST (a dead entry is reclaimed on any read via value-conditional TryRemove that takes only that exact entry, never a concurrent Store's replacement) -> token mismatch on a live entry returns false WITHOUT removal (the fix; mismatch flags a stale REQUEST, not a stale entry) -> match consumes via a single value-conditional TryRemove.
- Keying by deviceId+token was considered and rejected (the altitude review): it accumulates entries and reopens the JF-409 replay hole.
- Falsification-checked: with the pre-fix cache temporarily restored both new tests fail; with the fix both pass.

VERIFICATION
- Tests: Cache_TryGet_TokenMismatch_LeavesStoredEntryForMatchingConsumer (the AC#3 sequence exactly) + PlaybackNearlyFinished_DuplicateForPreviousTrack_DoesNotDestroyFreshEntry (handler-level replay of the live interleaving). Suite 2822 at the implementer's run, 2823 final; Release 0 warnings.
- Gates: /simplify (4 agents; findings applied: collapsed duplicated construct, trimmed duplicated doc narrative, removed a false Invalidate claim) + code-review high (its 2 beyond-scope findings filed as JF-424.1 stale-serve hole and JF-424.2 EntryTtl test seam).
- Live: rides the next bundle (unit-proven interleaving; not separately observable live without forcing Amazon to re-send NearlyFinished).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
