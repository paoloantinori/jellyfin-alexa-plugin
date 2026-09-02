---
id: JF-431
title: >-
  Pre-encode eviction sweep is synchronous full-directory I/O on the Alexa
  request path (pre-existing, never tracked)
status: Done
assignee:
  - zai
created_date: '2026-09-01 06:06'
updated_date: '2026-09-02 02:08'
labels:
  - code-review
  - latency
  - cache
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/VideoAudioCache.cs:272'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/VideoAudioCache.cs:284'
  - 'Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs:1582'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-428 review trail (2026-08-31): the always-true-bool finding became JF-428, but the OTHER half of that cut-list item was never tracked: EvictIfNeededCore runs a full cache-directory enumeration with per-file size scans (all *.mp4 files + all HLS dirs + per-file stats) SYNCHRONOUSLY inside the gated encode start, which sits on the Alexa request path (VideoAudioController -> StartFfmpegProcessGatedAsync -> EnsureDiskBudgetBeforeEncodeAsync). The JF-428 efficiency agent verified this is PRE-EXISTING (not worsened by JF-428) and it was left as an out-of-scope note. It is a latency-class concern in the ~8s Alexa budget that JF-358/JF-419 protect; large caches (2048MB default, thousands of HLS segments) make the scan nontrivial.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The pre-encode sweep (EnsureDiskBudgetBeforeEncodeAsync -> EvictIfNeededCore) no longer performs synchronous full-directory enumeration+per-file-size scan on the Alexa request path: either moved off the hot path (background/first-seen caching), made incremental, or measured and documented as provably cheap for realistic cache sizes
- [x] #2 A latency-oriented measurement (or test harness assertion) backs the decision; the JF-428 floor/pin semantics are unchanged
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Decision: Option A (measure-first, keep synchronous).** Measured 2026-09-02 with synthetic
sparse cache trees (created via `truncate`, so sizes are logical/apparent) enumerated with the
exact API shape `EvictIfNeededCore` uses (`DirectoryInfo.GetFiles`/`GetDirectories` +
`File.Exists(stream.m3u8)` + per-file `Length`/`LastAccessTimeUtc`; the minix runs use a Python
`os.scandir` + per-entry `stat` mirror of the same getdents64+statx syscalls, which is an upper
bound since .NET folds the stat into the enumeration). Median of 5 warm runs unless noted:

| Host | FS | Files | Apparent size | Cold (fadvise) | Warm median |
|---|---|---|---|---|---|
| minix (Intel N100, deploy target) | xfs | 2,250 (50 HLS dirs + 200 mp4s) | 2.0GB | 6.4ms | 6.3ms |
| minix (Intel N100) | xfs | 10,300 (100 dirs x 100 segs + 200 mp4s) | 1.8GB | 34.4ms | 29.7ms |
| minix (Intel N100) | xfs | 20,400 (200 dirs x 100 segs + 200 mp4s) | 3.3GB | 56.8ms | 56.5ms |
| dev (i7-1185G7) | tmpfs | 2,250 | 2.0GB | n/a | 3.5ms (prod shape) / 3.3ms (EnumerateFiles shape) |
| dev (i7-1185G7) | btrfs | 2,250 | 2.0GB | n/a | 3.3ms / 3.1ms |
| dev (i7-1185G7) | btrfs | 10,300 | 1.8GB | n/a | 14.5ms / 15.7ms |

Reading: a FULL default 2048MB cap built entirely of ~160KB 10s audiobook segments is ~13,000
files, so the closest measured row is the 10,300-file one (~29.7ms warm / 34.4ms cold on the
N100); linear extrapolation to the cap-filling ~13,000 files gives ~37ms warm / ~43ms cold,
still under the 50ms budget. Crossing 50ms on the N100 takes ~18,000 files (~1.4x the default
cap's file count). Even a hypothetical post-boot arctic cache (dcache dropped; not measurable
without root) at a 10x multiplier stays an order of magnitude under the ~8s Alexa window, and
the page-cache-warm steady state is the normal case because segment serving keeps the tree
warm. Option B (cached-size ledger) was rejected: a stale-HIGH total could over-evict or a
ledger adds sweep-on-write overhead, and at these measured sizes there is nothing to win;
JF-428's pin-before-sweep + half-cap-floor semantics rely on the sweep running synchronously
before every encode.

**Code change (Option A):** `EvictIfNeededCore` now wraps the enumeration phase in a
Stopwatch: scan duration logged at Debug on every sweep, and at Information when it exceeds
`SlowEvictionScanThresholdMs` (50ms) as a tripwire so any deployment whose scan is no longer
provably cheap (oversized cap, slow storage) surfaces in default-level logs. The measurement
table is documented in a comment at the scan site. No behavior change: pin ordering, floor
semantics, and the run-before-every-encode contract are untouched (JF-428 tests stay green).

AC#1 is satisfied via the "measured and documented as provably cheap" branch; AC#2 via the
table above plus the in-code tripwire.

**Simplify pass (2026-09-02, 4 parallel reviewers on the diff).** Applied: the tripwire now
fires before the `entries.Count == 0` early return (a slow scan yielding zero entries was
silent before); the const's XML doc shrunk to a pointer so the measurement lives in one place;
MB log placeholders use the file's `F1` convention; the measurement comment now attributes the
N100 numbers to the scandir+stat mirror rather than "the exact APIs", states the full-cap
extrapolation (~13,000 files = ~37ms warm / ~43ms cold) instead of the optimistic "~35ms",
says "an order of magnitude" instead of "orders", "up to ~16%" for the fadvise penalty,
3.5ms for the tmpfs row, "~1.4x the default cap's file count" for the 50ms crossing, and the
task note's 34.4ms. Declined: dropping the "(JF-431)" tag from the Information template
(inline task tags match codebase-wide log practice, e.g. PlaybackStartedEventHandler/PlayAlbum);
hoisting the MB division into a local (the file's existing logs inline the same expression).
One reviewer confirmed the change is pure observability (JF-428 pin/floor/sweep ordering
untouched, build 0 warnings).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-431: the synchronous eviction sweep is a measured decision with a live tripwire, hardened against the permission-wedge crash.

WHAT CHANGED (commit 0354e51)
- DECISION (Option A, measure-first): the sweep stays synchronous. Synthetic sparse cache trees, production API shape, median of 5 warm runs on the N100 deploy target: 2,250 files/2.0GB -> 6.3ms; 10,300/1.8GB -> 29.7ms warm / 34.4ms cold; realistic worst case (full 2048MB cap of 160KB segments, ~13,000 files) -> ~37ms warm / ~43ms cold, under the 50ms budget. Option B (cached-size ledger) rejected: stale-total risk for no measurable win; the JF-428 pin/floor semantics need the synchronous sweep. The full table lives at the scan site + task notes.
- The sweep now times itself: Debug always, Information past SlowEvictionScanThresholdMs (50) as a tripwire, called from a finally so I/O-FAILED scans also report (review finding: stalling storage ending in IOException was the exact case the tripwire existed for and it was silent).
- REVIEW-ROUND CRITICAL FIX: the UnauthorizedAccessException wedge. One root-owned cache entry (the documented podman-cp incident class) escaped every catch, failed the Alexa play endpoint, leaked the pre-encode pin (Pin -> unwrapped sweep await; all Unpin sites downstream), and re-failed every subsequent encode while the cache stayed over cap: one damaged entry wedged ALL uncached plays until restart. Fixed: UAE catches at all three sites (per-dir scan continue; outer return; per-entry delete skip with a Warning naming the path, other entries still evict) + the pin released on the sweep-throw path in StartFfmpegProcessGatedAsync. Pin-leak CONFIRMED by line evidence pre-fix.

VERIFICATION
- 3 new permission-simulation tests (real chmod 555/111/000, Linux-guarded, root-bypass probes, finally-restored perms); RED-PROVEN via stash-revert (all 3 failed with the unhandled UAE pre-fix). Suite 2832 -> 2841/2841; Release 0 warnings; JF-428's 24 cache tests green (pure observability + crash-hardening, no semantic change).
- Gates: /simplify (4 agents; tripwire-before-empty-return, doc dedup, wording corrections); code-review high (9 findings: the UAE wedge + the tripwire blind spot applied same-night; 7 filed as JF-448).
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
- [x] #10 /code-review high passed (no blocking findings remaining
- [x] #11 or findings applied/tracked)
<!-- DOD:END -->
