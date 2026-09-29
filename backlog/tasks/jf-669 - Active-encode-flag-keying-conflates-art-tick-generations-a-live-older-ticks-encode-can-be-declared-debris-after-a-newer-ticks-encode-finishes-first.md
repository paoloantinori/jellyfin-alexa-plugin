---
id: JF-669
title: >-
  Active-encode flag keying conflates art-tick generations: a live older-ticks
  encode can be declared debris after a newer-ticks encode finishes first
status: To Do
assignee: []
created_date: '2026-09-29'
labels:
  - encode-gate
  - race
  - cache
dependencies: []
references:
  - >-
    backlog/tasks/jf-665 - Registration-side-of-the-same-key-speed-encode-re-register-race-orphaned-displaced-ffmpeg-at-the-registry-overwrite-unchecked-TryAdd-key-only-encode-flag-clear.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-29 same-turn from the JF-665 code-review round (high effort, finding 1 of 5). INHERITED exposure, not a JF-665 regression: JF-665's generation tokens made the clears strictly safer in this scenario (before, BOTH generations' key-only clears dropped the flag; after, only the newest generation's clear can), but the newest-generation-owns-the-flag contract still has no answer for CROSS-TICKS displacement.

THE EXPOSURE: the three active-encode flag registries are keyed by the bare cache key (episode/song itemId, audiobook parentId, variant cache key), while the per-key LOCKS and the CACHE DIRECTORIES are keyed by (key, artModifiedTicks). When an item's art changes mid-encode:

1. Episode X encodes under ticks=A (flag token T_A set, monitor running, dir {key}_A live).
2. Art changes; a second request computes ticks=B, takes lock(key, B) (a DIFFERENT lock), and MarkEncodeActive displaces T_A with T_B under the SAME flag entry.
3. The gen-B encode finishes FIRST; its finally compare-and-removes T_B, so the flag drops while the gen-A encode is still live.
4. A request that read ticks=A before the art change then sees ContainsKey(key)==false plus a no-ENDLIST playlist in dir_A, and ValidateEpisodeCacheAsync returns the interrupted-debris verdict: it Cleans up dir_A mid-encode and starts a duplicate encode, wasting the live encode's output.

Same shape on the song path (MarkEncodeActive after the prewrite) and the audiobook path (keyed by parentId).

REACHABILITY: needs an art change mid-encode plus a tick-stale request; rare, but the deletion of a live encode's directory is the same damage class as the JF-499 W3 vanish the fast paths already guard.

FIX DIRECTION (not done; needs its own design pass): make flag liveness art-tick aware, either by keying the flag entry by (key, artTicks) with presence readers falling back across tick generations, or by refcounting generations under one entry so the flag drops only when the LAST live generation exits. Every presence reader (ValidateEpisodeCacheAsync, the near-ahead hold, the prewrite serve gates, the concurrent-encode guards) reads by bare key today, so this is a semantics change across serve paths, deliberately not folded into JF-665's minimal fix.

VERIFICATION: a test that starts a ticks=A encode (fake ffmpeg), marks a ticks=B generation, lets the B monitor clear, and asserts the A directory is not treated as debris while A still runs.
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
