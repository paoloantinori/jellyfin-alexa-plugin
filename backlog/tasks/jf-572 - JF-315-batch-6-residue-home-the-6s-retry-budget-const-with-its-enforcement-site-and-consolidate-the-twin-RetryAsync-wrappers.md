---
id: JF-572
title: >-
  JF-315 batch-6 residue: home the 6s retry-budget const with its enforcement
  site and consolidate the twin RetryAsync wrappers
status: To Do
assignee: []
created_date: '2026-09-15 22:58'
labels: []
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/RetryHelper.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/SearchService.cs
  - Jellyfin.Plugin.AlexaSkill/Controller/AlexaSkillController.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
JF-315 batch 6 extracted SearchService and left two deliberate-but-temporary seams that this task consolidates (both flagged by the batch-6 /simplify altitude review, deferred there to keep the extraction a pure verbatim move):

1. THE TWIN RetryAsync WRAPPERS. BaseHandler.RetryAsync (Alexa/Handler/BaseHandler.cs, ~line 1267) and SearchService.RetryAsync (Alexa/Util/SearchService.cs, tail) are now two identical private wrappers delegating to RetryHelper.ExecuteWithRetryAsync with the same 6s budget. This mirrors JF-570 (the twin SortByRating overloads split across BaseHandler and ResumeMath by the same batch-2 verbatim-move discipline). Consolidation direction: the shared wrapper belongs with the budget's true owner (see item 2) or on RetryHelper itself, with BaseHandler and SearchService keeping only a thin call.

2. THE BUDGET CONST'S TRUE HOME. AlexaRequestTimeoutMs is a private const on BaseHandler (6000, documented as matching AlexaSkillController's CancellationTokenSource(TimeSpan.FromSeconds(6)) at Controller/AlexaSkillController.cs ~line 399). Batch 6 passed it into SearchService at composition (the PlaybackLaunchBuilder delegate-seam precedent) so Util holds no Handler-layer reference, but the linkage is still by convention: the controller hardcodes its own 6 and reads no const. The review's recommendation: home the budget as a public const on RetryHelper (whose test suite already names the budget invariant, see RetryHelperTests.Sync_AlwaysTransient_StopsWithinTimeoutBudget), have RetryHelper.ExecuteWithRetryAsync's timeoutMs default to it, and make AlexaSkillController's CTS consume it; BaseHandler's const then aliases or deletes.

Context you need: the budget is behavior-load-bearing (it is the mechanism that keeps slow play-path queries inside Alexa's ~8s response window, see the repo CLAUDE.md coverage caveat), so any change must keep BOTH the value (6000) and the single-source property. The suite is the safety net (~3919 facts both TFMs; never dotnet test --no-build after changes).
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
