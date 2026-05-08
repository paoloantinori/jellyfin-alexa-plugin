---
id: JF-84
title: Timeout-aware retry budget in RetryHelper
status: Done
assignee: []
created_date: '2026-05-06 19:22'
updated_date: '2026-05-06 21:34'
labels:
  - resilience
milestone: m-2
dependencies: []
references:
  - claudedocs/research_alexa_best_practices_2026-05-06.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Pass the CancellationToken and remaining-time awareness into the retry logic in `RetryHelper.cs`. Currently a handler might spend 4s on its first attempt, retry after 1s delay, then timeout at 6s — wasting the delay time when the outcome is already certain.

Before each retry, check: `elapsed + nextDelay + estimatedMinOperationTime > timeoutBudget`. If so, skip the retry and return the best available response.

The controller already creates `CancellationTokenSource(TimeSpan.FromSeconds(6))` — the retry logic should be aware of this budget.

Files: `Jellyfin.Plugin.AlexaSkill/Alexa/RetryHelper.cs`, callers in `BaseHandler.cs`.

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 2.3
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 CancellationToken passed through to RetryHelper from calling code
- [ ] #2 Before each retry, check if elapsed + nextDelay + estimatedMinOperationTime exceeds timeout budget
- [ ] #3 If budget exceeded, skip retry and return best available response
- [ ] #4 Existing tests pass; new tests verify budget-aware retry skipping
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added optional `timeoutMs` parameter to both `RetryHelper.ExecuteWithRetryAsync` overloads. When set, a `Stopwatch` tracks elapsed time; before each retry, the method checks if `elapsed + delay + minOperationMs` exceeds the budget and skips the retry if so. `BaseHandler` passes `AlexaRequestTimeoutMs` (6000ms) to all retry calls. Extracted `IsBudgetExceeded` helper to eliminate duplication between the two overloads. 6 new unit tests cover budget-exceeded, sufficient-budget, tight-budget, and backward-compatibility scenarios. All 686 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
