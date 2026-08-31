---
id: JF-428
title: >-
  Encode cache budget: undersized config clamps eviction target to 0 (wipes ALL
  entries incl. the in-use HLS dir) + admission check never refuses and
  under-reserves 64MB vs ~472MB encodes
status: To Do
assignee: []
created_date: '2026-08-31 19:32'
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
- [ ] #1 EvictIfNeededCore no longer clamps maxSizeBytes below a safe floor: a configured VideoAudioCacheSizeMB smaller than the encode headroom must never produce a zero/negative target (decide: floor the target, skip the headroom subtraction, or refuse the encode - document the choice)
- [ ] #2 The directory of an item currently being encoded/streamed is never evicted mid-stream (in-use protection or reservation), and a regression test proves segment fetches survive a cache-pressure event during encode
- [ ] #3 EnsureDiskBudgetBeforeEncodeAsync enforces a real admission decision or is removed: either it can refuse/gate an encode (with a documented policy), or the always-true bool and its call site go away
- [ ] #4 The reserved headroom reflects actual encode size (scale by content duration or a documented conservative bound), not a flat 64MB
- [ ] #5 Unit tests: undersized-config clamp scenario, in-use-dir survival, headroom computation
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
