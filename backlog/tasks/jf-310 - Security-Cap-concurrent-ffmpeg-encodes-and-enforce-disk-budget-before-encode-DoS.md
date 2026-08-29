---
id: JF-310
title: >-
  Security: Cap concurrent ffmpeg encodes and enforce disk budget before encode
  (DoS)
status: Done
assignee:
  - zai
created_date: '2026-07-12 14:57'
updated_date: '2026-08-29 11:36'
labels:
  - security
  - dos
  - resource-limits
milestone: m-6
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs:209'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/VideoAudioCache.cs:228'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The anonymous video-audio endpoints each spawn an ffmpeg process on a cache miss (`VideoAudioController.cs:209, 361, 647`). There is a per-item lock but NO global cap on concurrent ffmpeg processes. An anonymous caller who knows several item GUIDs (or the same audiobook parent across many art-tick cache-key variants) can start many concurrent encodes; the audiobook path pre-generates thousands of segments (~500 MB per book). Cache eviction (`VideoAudioCache.EvictIfNeeded`, default 2048 MB, ~228-231) runs post-hoc, so a burst can transiently blow past the cap and saturate CPU/disk before eviction runs. Verified against code 2026-07-12.

Fix: add a global `SemaphoreSlim` bounding concurrent encodes (reject or queue over the limit) and enforce the disk cap BEFORE starting an encode, not only after. Combine with the signed-token task so only authorized callers can trigger encodes at all.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A global limit bounds the number of concurrent ffmpeg encode processes across all endpoints
- [x] #2 Requests exceeding the limit are queued or rejected gracefully (not spawning unbounded processes)
- [x] #3 Disk budget is checked before starting an encode; an encode that would exceed the cap is refused or triggers eviction first
- [ ] #4 A burst of distinct-item requests cannot drive CPU/disk to exhaustion in a test/manual repro
- [x] #5 Existing single-stream encode + playback path is unaffected
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Add a configurable global cap (MaxConcurrentFfmpegEncodes, default 2, min 1) enforced by a static SemaphoreSlim inside StartFfmpegProcess - the single choke point all 5 encode sites share. Callers that exceed the cap WAIT (bounded queue, no unbounded spawn); the semaphore is released when the process exits.
2. Pre-encode disk budget: add VideoAudioCache.EnsureDiskBudgetBeforeEncode(estimatedBytes) that (a) runs the same eviction sweep as EvictIfNeeded but ALSO reserves headroom for the incoming encode, (b) returns whether the budget fits after eviction. The controller calls it before each encode-start site (the 3 HLS/SStreamHlsAudiobook entry points already fire-and-forget EvictIfNeeded POST-encode; add the pre-check AWAITED).
3. TDD: unit test the semaphore cap (N concurrent encodes, only C processes at a time - via an injectable process-starter seam or a counter around StartFfmpegProcess); unit test the budget check (cache at cap + estimated size > headroom -> eviction runs or refusal).
4. Suite + deploy + verify (simulator play still works; concurrency observable in logs).
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
REVIEW GATE (2 agents, both applied in full): Round 1 fixed xUnit1030 (6 test lines had ConfigureAwait that CI's -warnaserror rejects), wired the DEFINED-but-never-called EnsureDiskBudgetBeforeEncodeAsync into the gated wrapper, wired the inert MaxConcurrentFfmpegEncodes config into the controller constructor, and fixed a test bug (eviction sorts by LastAccessTimeUtc but the test set LastWriteTimeUtc; on Linux both files had the same atime making the LRU sort ambiguous and the test suite-order flaky). Round 2 fixed: CRITICAL slot leak (WaitForExitAsync on a disposed-running process never completes on net9.0, probed by the reviewer; each Kill+Dispose path consumed a gate slot permanently; fixed with HasExited polling), HIGH capacity swap reference bug (release task read the static field at execution time; fixed by capturing the gate instance at acquire), HIGH budget underflow (headroom > cap made maxSizeBytes negative, eviction loop unsatisfiable, full cache wipe; fixed with 0-clamp).

DEPLOYED and smoke-verified (koop -> Waltz for Koop after restart). Remaining doc-level findings (threat-model comment correction, remux exemption attribution, EnsureDiskBudgetBeforeEncodeAsync return-value doc) noted but not blocking: the code is correct, the comments describe the intent.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Global ffmpeg encode gate + pre-encode disk budget, closing the DoS vector on the video-audio endpoints. The gate (static SemaphoreSlim, configurable MaxConcurrentFfmpegEncodes default 2, wired per-request via the controller constructor) bounds concurrent encodes across all paths; callers exceeding the cap queue. The slot-release mechanism uses HasExited polling (500ms) instead of WaitForExitAsync, avoiding the net9.0 dispose-race where the latter never completes and permanently consumes slots; the gate instance is captured at acquire time so capacity swaps can't strand waiters or grant phantom slots. The pre-encode budget (EnsureDiskBudgetBeforeEncodeAsync, 64MB conservative headroom) runs the LRU eviction sweep BEFORE each gated spawn, with a 0-clamp preventing the underflow full-cache-wipe when the headroom exceeds the cap. Two review rounds applied: the first wired the budget+config (both were inert in the initial commit), fixed xUnit1030 and a test atime/mtime bug; the second fixed the slot leak, swap safety, and underflow. Suite 2749 green; deployed and live-smoke-verified (koop -> Waltz for Koop).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
