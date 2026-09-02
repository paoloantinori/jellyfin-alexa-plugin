---
id: JF-428
title: >-
  Encode cache budget: undersized config clamps eviction target to 0 (wipes ALL
  entries incl. the in-use HLS dir) + admission check never refuses and
  under-reserves 64MB vs ~472MB encodes
status: Done
assignee:
  - zai
created_date: '2026-08-31 19:32'
updated_date: '2026-09-01 04:12'
labels:
  - code-review
  - cache
  - playback
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/VideoAudioCache.cs:293'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/VideoAudioCache.cs:272'
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two code-review findings (2026-08-31 third high pass, both CONFIRMED by direct code reading) in the video-audio encode cache admission/eviction mechanism. VideoAudioCache.cs:272 (EnsureDiskBudgetBeforeEncodeAsync) and :293 (EvictIfNeededCore negative clamp).

DEFECT 1 (severe, :293): the negative clamp sets maxSizeBytes to 0 when the 64MB encode headroom exceeds the configured VideoAudioCacheSizeMB; the eviction loop then deletes EVERY entry, including the HLS directory of the audiobook currently being encoded and streamed. Scenario: VideoAudioCacheSizeMB=50 on a small disk; encode writes ~40MB of segments the Echo Show is streaming; next gated encode start calls EnsureDiskBudgetBeforeEncodeAsync(64MB) -> 50-64 clamps to 0 -> totalSize(40MB) > 0 -> deletes oldest-first until <= 0, i.e. ALL entries including the in-use directory -> segment fetches 404, playback stalls mid-book; every subsequent encode start repeats the wipe. The comment claims the clamp prevents the wipe, but clamping to 0 still TARGETS zero.

DEFECT 2 (:272): EnsureDiskBudgetBeforeEncodeAsync always returns true (no refusal path), the caller discards the bool, and the headroom is a hardcoded 64MB regardless of content duration (~472MB for an 8.3h audiobook per repo docs), so the pre-encode admission decision enforces nothing beyond the ordinary eviction sweep; no free-space check at all; transient can overshoot the configured cap by (encodeSize - 64MB) per in-flight encode.

Related but separate: jf-421 tracks the semaphore swap that decides WHEN encodes start; this task is the disk-budget/eviction correctness once they do.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 EvictIfNeededCore no longer clamps maxSizeBytes below a safe floor: a configured VideoAudioCacheSizeMB smaller than the encode headroom must never produce a zero/negative target (decide: floor the target, skip the headroom subtraction, or refuse the encode - document the choice)
- [x] #2 The directory of an item currently being encoded/streamed is never evicted mid-stream (in-use protection or reservation), and a regression test proves segment fetches survive a cache-pressure event during encode
- [x] #3 EnsureDiskBudgetBeforeEncodeAsync enforces a real admission decision or is removed: either it can refuse/gate an encode (with a documented policy), or the always-true bool and its call site go away
- [x] #4 The reserved headroom reflects actual encode size (scale by content duration or a documented conservative bound), not a flat 64MB
- [x] #5 Unit tests: undersized-config clamp scenario, in-use-dir survival, headroom computation
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Implementation Plan (JF-428)

**Approach**: three mechanisms, all inside the existing sweep; no admission-refusal policy (the existing doc comment already decides an oversized single item is a config problem, not a DoS vector; the always-true bool goes away instead).

**VideoAudioCache.cs:**
1. Eviction floor (AC #1): target = Math.Max(configBytes - headroom, configBytes / 2). An oversized reservation can evict at most half the cache in the pre-encode sweep; the post-encode sweep (headroom 0) still enforces the full cap. Replaces the clamp-to-0 (which still TARGETED zero).
2. Pin registry (AC #2): ConcurrentDictionary<string, byte> + Pin(path)/Unpin(path); eviction skips pinned entries (size still counted; logs once when the target is unreachable because pinned entries hold the excess and stops instead of deleting everything else trying).
3. EnsureDiskBudgetBeforeEncodeAsync returns Task (bool removed - AC #3, no-refusal documented decision).

**VideoAudioController.cs:**
4. StartFfmpegProcessGatedAsync gains (long estimatedEncodeBytes, string? pinPath): pin before ffmpeg starts, unpin in the existing process-exit poll loop (next to the gate release).
5. EstimateEncodeBytes(long runtimeTicks): max(64MB, durationHours * 64MB). Doc: measured 472MB/8.3h ~= 57MB/h (audio copy + 1fps CRF51 video), 64MB/h with margin; the old flat 64MB was implicitly the 1h case and under-reserved >1h content.
6. All 4 call sites pass the estimate (item.RunTimeTicks or chapters sum) + pin path (hlsDir / mp4 file). No semaphore changes (jf-421 is separate).

**Tests (VideoAudioCacheTests + controller helper, red first):**
1. EvictIfNeeded_HeadroomExceedsCap_ClampsToHalfCapNotZero (AC #1): config 100MB, 3 entries ~90MB total, headroom 150MB -> oldest evicted until <= 50MB, newest SURVIVES (old code wiped all).
2. EvictIfNeeded_PinnedEntryIsNeverEvicted (AC #2): pin the oldest over-limit entry -> pinned survives, others evicted; pinned-only-over-limit -> nothing deleted.
3. EstimateEncodeBytes_ScalesWithDuration_FloorsAt64MB (AC #4/#5).

**Verification**: dotnet build + full dotnet test, /simplify + /code-review gates, commit with markers. Live verify: unit-level only (cache pressure on the live box would require a real undersized config; note as deferred).
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-08-31 TDD progress: 3 new cache tests red-first (floor, pinned-survives, pinned-only-deletes-nothing), then green; 2 existing budget tests recalibrated for the floor semantics (one collided by design: ~1MB headroom on 1MB cap now floors at 512KB and evicts nothing, which IS the fix); EstimateEncodeBytes theory test added. Suite 2774/2767+7 green. Dead RunFfmpegToCompletionAsync (zero prod+test callers) deleted instead of rewiring its signature.

Altitude agent verdict: correctly scoped across the board. Follow-up note (not a redesign): after the encode ends the dir is unpinned and protected only by LRU recency (verified to hold: FindSegmentPath/GetCachedHlsPlaylist RecordAccess on every serve, ascending sort puts the streamed dir last); a cached-but-not-encoding dir can still be evicted mid-stream when it alone exceeds cap/2 under an undersized cap - same family, surfaced by the new over-target warning; a stream-scope pin would defeat LRU for 8h books, recency is the right default.

2026-08-31 /simplify (4 agents) applied:

- pinPath made REQUIRED (all 3 call sites pass it): 3 null-checks deleted

- floor as one-line Math.Max(cap - headroom, cap/2) matching the file idiom

- EnsureDiskBudgetBeforeEncodeAsync doc trimmed to 3 lines (pass-through); the undersized-config incident story now lives ONCE, at the floor comment

- pin-skip comment + over-target warning reworded truthfully (pins AND/OR failed deletes; the warning can fire for IOException failures too, not only pins)

- Skips with reasons: _pinnedPaths vs _itemLocks overlap (reuse agent, minor: HLS dirs are not lock keys, windows differ, one uniform mechanism for 3 sites beats two; altitude agent independently judged the pin correctly scoped); gate+pin releaser holder (2 call sites, abstraction costs more than 3 duplicated lines); SinglePinnedOverLimit test kept (documents the unreachable-target stop scenario distinctly); warning dedup/state-tracking skipped (volume is per-encode-start, log hygiene only).

Efficiency agent verified clean: pre-encode full sweep is PRE-EXISTING (not worsened; the floor actually reduces worst-case deletion work); no warning spam possible (EvictIfNeeded fires only at encode completion, not per segment); EstimateEncodeBytes overflow-safe (division first). Full suite 2774/2774 after fixes. /code-review high running.

2026-08-31 /code-review high on THIS diff returned 6 findings, all applied in be445a1: (1) EstimateEncodeBytes truncated fractional hours (1.9h reserved the 64MB floor instead of ~128MB; now ceiling), (2) _pinnedPaths doc overclaimed the protection window + faststart remux never pinned (doc scoped to write window; remux now pinned), (3) unrefcounted pin + delayed exit-poll release could expose a retrying encode's re-pinned entry (now refcounted, with regression test), (4) pin leaked if gate WaitAsync threw (pin moved after acquire), (5) pin-skip comment described behavior the loop does not have (reworded to actual: all unpinned entries evict oldest-first when target unreachable), (6) prose-hyphens in authored comments/notes (fixed). Committed with gates; suite 2776/2776.

2026-09-01 DEPLOYED to minix (DLL b79870d9, bundled with JF-419.2); boot + playback matrix green. Cache floor/pin behavior remains unit-verified (live proof would need an undersized VideoAudioCacheSizeMB on the live box; not worth risking real playback).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-428: the encode cache can no longer wipe itself (or the in-use HLS dir) on undersized configs, and in-flight writes are eviction-proof.

WHAT CHANGED (commit be445a1; VideoAudioCache.cs + VideoAudioController.cs + 2 test files)
- Eviction floor: the pre-encode target floors at half the configured cap (Math.Max(cap - headroom, cap/2)), replacing the clamp-to-0 that still TARGETED zero and deleted every entry on undersized configs (mid-book segment 404s, wipe at every encode start). The post-encode sweep still enforces the full cap.
- In-use pinning, refcounted: Pin/Unpin on VideoAudioCache; eviction skips pinned entries while their size still counts. Wired at all 3 ffmpeg encode paths (pin after gate acquire so a cancelled wait cannot leak; unpin in the exit-poll loop and on start failure) AND the faststart remux (previously unpinned while writing .fs.mp4 into the cache). Refcounting closes the review-found race where a delayed exit-poll release exposed a retrying encode's re-pinned entry.
- EstimateEncodeBytes: duration-scaled reservation (64MB/h from the measured 472MB/8.3h rate), fractional hours rounded UP (1.5h reserves 128MB, not the 64MB floor), one-hour floor. Replaces the flat 64MB.
- EnsureDiskBudgetBeforeEncodeAsync: always-true bool removed; the no-refusal policy is documented (oversized single item is a config problem, not a DoS vector).
- Dead RunFfmpegToCompletionAsync (zero prod+test callers) deleted instead of rewiring its signature.

WHY: code-review CONFIRMED that VideoAudioCacheSizeMB=50 + 64MB headroom clamped the eviction target to 0 and deleted everything including the directory being streamed; the flat 64MB also under-reserved >1h encodes ~7x.

VERIFICATION
- TDD red-first: floor test failed on old code (newest entry wiped), pinned tests failed on missing API, ceiling case added after review. Full suite 2767 -> 2776 passed / 0 failed; Release build 0 warnings (TreatWarningsAsErrors).
- Gates: /simplify 4-agent pass (pinPath made required, floor one-liner, doc dedup, truthful warning; skips documented: _itemLocks overlap, releaser holder, warning state-tracking); /code-review high found 6 findings ON THIS DIFF and ALL 6 were applied (ceiling rounding, refcount race, pin-after-acquire leak, remux pin, comment accuracy, prose-hyphens) - the gates demonstrably earned their keep on this task.
- AC #2 scope note: the pin covers the WRITE window; post-encode streaming protection is LRU serve-recency (RecordAccess on every segment/playlist serve puts the streamed dir last in the ascending sort; verified line-by-line). Residual: a cached-but-not-encoding dir larger than cap/2 under an undersized cap can still be evicted mid-stream; surfaced by the new over-target warning, accepted trade-off (a stream-scope pin would defeat LRU for 8h books).
- Live verify deferred (would need a real undersized config on minix); unit tests cover the mechanism. Next DLL deploy carries this fix.
- DoD 4/5/8 N/A (no session attrs, no HttpClient changes, no locale strings); DoD 7 N/A (cache internals, not handler logic; covered by unit tests).
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
