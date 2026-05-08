---
id: JF-79
title: Add jitter to RetryHelper exponential backoff
status: Done
assignee: []
created_date: '2026-05-06 19:20'
updated_date: '2026-05-06 19:56'
labels:
  - resilience
  - high-priority
milestone: m-2
dependencies: []
references:
  - claudedocs/research_alexa_best_practices_2026-05-06.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add randomized jitter to the exponential backoff delay in `RetryHelper.cs` to prevent thundering herd when multiple requests retry simultaneously against a struggling Jellyfin server.

Current code: `initialDelayMs * (int)Math.Pow(2, attempt)` — pure exponential, all clients retry at identical intervals.

Target: `initialDelayMs * 2^attempt + random(0, initialDelayMs/2)` — adds up to half the base delay as random jitter.

Files: `Jellyfin.Plugin.AlexaSkill/Alexa/RetryHelper.cs`

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 2.1
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 RetryHelper async and sync retry paths include random jitter in delay calculation
- [x] #2 Jitter range is 0 to initialDelayMs/2 added to exponential delay
- [x] #3 Existing unit tests pass and new test(s) verify jitter is non-deterministic but bounded
- [x] #4 No change to default retry count (3) or initial delay (500ms)
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added `CalculateDelay()` method with `random(0, initialDelayMs/2)` jitter on top of exponential backoff. Both sync and async retry paths now use the shared method. Four new tests verify bounded range, non-determinism, zero-attempt behavior, and small-delay edge case. Defaults unchanged (3 retries, 500ms).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->
