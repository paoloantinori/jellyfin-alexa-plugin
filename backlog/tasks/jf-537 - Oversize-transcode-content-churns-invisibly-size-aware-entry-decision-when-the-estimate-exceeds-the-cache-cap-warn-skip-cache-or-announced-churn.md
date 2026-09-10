---
id: JF-537
title: >-
  Oversize transcode content churns invisibly: size-aware entry decision when
  the estimate exceeds the cache cap (warn + skip-cache or announced churn)
status: To Do
assignee: []
created_date: '2026-09-10 10:39'
labels:
  - video
  - hls
  - cache
  - ux
  - jf534-followup
dependencies: []
references:
  - JF-534
  - JF-500
  - VideoAudioController.cs EstimateEpisodeTranscodeEncodeBytes
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-534 /simplify altitude review (2026-09-10): with the default cap at 4096MB and the transcode tier's ~3GB/h rate, any content beyond ~80 minutes has an estimate EXCEEDING the cap, so it churns invisibly: every replay re-encodes from zero under any eviction policy (oldest-first deletes it right after completion; pinning it would starve everything else - explicitly rejected by the review). Concrete scenario: a 2h HEVC movie writes ~6GB against the 4096 cap; each replay pays a full encode with zero cache benefit and the user has no way to notice.

Fix direction: a size-aware decision at transcode encode ENTRY - when EstimateEpisodeTranscodeEncodeBytes(runtime) > configured cap, log a Warning naming the required cap (e.g. 'item X needs ~6GB, cache cap is 4096MB; it cannot be retained - consider raising the cap') and decide deliberately: (a) stream without caching that item (skip the cache write; still encode for playback), or (b) encode + accept documented churn (today's behavior, but ANNOUNCED). Option (a) is likely right but check the encode-path plumbing (the monitor, pinning, and playlist serving assume a cache dir; a no-cache mode may need a temp-dir variant - scope before committing). Also surface the condition on the config page's cache description ('content longer than ~80min at 3GB/h cannot be retained at this cap').
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
