---
id: JF-431
title: >-
  Pre-encode eviction sweep is synchronous full-directory I/O on the Alexa
  request path (pre-existing, never tracked)
status: To Do
assignee: []
created_date: '2026-09-01 06:06'
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
- [ ] #1 The pre-encode sweep (EnsureDiskBudgetBeforeEncodeAsync -> EvictIfNeededCore) no longer performs synchronous full-directory enumeration+per-file-size scan on the Alexa request path: either moved off the hot path (background/first-seen caching), made incremental, or measured and documented as provably cheap for realistic cache sizes
- [ ] #2 A latency-oriented measurement (or test harness assertion) backs the decision; the JF-428 floor/pin semantics are unchanged
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
