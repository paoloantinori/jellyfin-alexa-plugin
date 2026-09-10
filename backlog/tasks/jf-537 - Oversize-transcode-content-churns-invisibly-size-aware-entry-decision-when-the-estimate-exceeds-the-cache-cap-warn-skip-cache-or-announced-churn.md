---
id: JF-537
title: >-
  Oversize transcode content churns invisibly: size-aware entry decision when
  the estimate exceeds the cache cap (warn + skip-cache or announced churn)
status: Done
assignee: []
created_date: '2026-09-10 10:39'
updated_date: '2026-09-10 20:45'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed 2026-09-10 with merge 826e1010 + deployed (clean rebuild for the embedded config.html; md5 95516fa5 verified; config survived Users=1; served page carries the rounded-hour sentence; zero FTL). Mode (b) ANNOUNCED CHURN: a Warning at transcode encode entry (once per encode start, cache-miss/in-lock only) naming item/estimate/cap + the raise-the-cap advice, a Debug cacheable line otherwise; TranscodeEstimateExceedsCacheCap (strictly-greater, matching the sweep's non-strict retention) beside the estimator; estimate computed once and shared with the JF-428 budget; EffectiveCacheCapMB + DefaultCacheCapMB unify the cap read with the sweep; config.html documents the rounded-hour boundary. The 5-point no-cache rejection (segment resolution, dedup, no playback-stop signal, budget regression, unbounded disk) verified independently by both gates; the deferred transient mode tracked as JF-537.1. Gates: /simplify (its accuracy finding applied - the linear ~80min claim corrected to the round-up estimator's real 60-minute boundary in config.html AND the estimator doc; plus comment trim, hoisted local, and the review's two wording precisions), code-review high ZERO findings (all six points independently confirmed), tests 3589/3589 net9.0 (worker + orchestrator + reviewer), 0 warnings both TFMs. Remux/audio/audiobook tiers excluded with rationale.
<!-- SECTION:FINAL_SUMMARY:END -->
