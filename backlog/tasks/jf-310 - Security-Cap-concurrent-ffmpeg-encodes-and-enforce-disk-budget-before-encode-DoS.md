---
id: JF-310
title: >-
  Security: Cap concurrent ffmpeg encodes and enforce disk budget before encode
  (DoS)
status: In Progress
assignee:
  - zai
created_date: '2026-07-12 14:57'
updated_date: '2026-08-29 09:15'
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
- [ ] #1 A global limit bounds the number of concurrent ffmpeg encode processes across all endpoints
- [ ] #2 Requests exceeding the limit are queued or rejected gracefully (not spawning unbounded processes)
- [ ] #3 Disk budget is checked before starting an encode; an encode that would exceed the cap is refused or triggers eviction first
- [ ] #4 A burst of distinct-item requests cannot drive CPU/disk to exhaustion in a test/manual repro
- [ ] #5 Existing single-stream encode + playback path is unaffected
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Add a configurable global cap (MaxConcurrentFfmpegEncodes, default 2, min 1) enforced by a static SemaphoreSlim inside StartFfmpegProcess - the single choke point all 5 encode sites share. Callers that exceed the cap WAIT (bounded queue, no unbounded spawn); the semaphore is released when the process exits.
2. Pre-encode disk budget: add VideoAudioCache.EnsureDiskBudgetBeforeEncode(estimatedBytes) that (a) runs the same eviction sweep as EvictIfNeeded but ALSO reserves headroom for the incoming encode, (b) returns whether the budget fits after eviction. The controller calls it before each encode-start site (the 3 HLS/SStreamHlsAudiobook entry points already fire-and-forget EvictIfNeeded POST-encode; add the pre-check AWAITED).
3. TDD: unit test the semaphore cap (N concurrent encodes, only C processes at a time - via an injectable process-starter seam or a counter around StartFfmpegProcess); unit test the budget check (cache at cap + estimated size > headroom -> eviction runs or refusal).
4. Suite + deploy + verify (simulator play still works; concurrency observable in logs).
<!-- SECTION:PLAN:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
