---
id: JF-366
title: Make InteractionModelRedeployer unit-testable (extract ISmapiManagement seam)
status: To Do
assignee: []
created_date: '2026-07-24 19:17'
labels:
  - testing
  - tech-debt
  - redeploy
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live testing of PR #14 caught a reporting bug in `InteractionModelRedeployer.RedeployAsync`: the poll loop (`PollLocaleBuildStatusAsync`) returns status for every active locale on the skill, so a single-locale rebuild was reported as "1 locale rebuilt — 11 succeeded, 1 failed" with stale statuses from untouched locales bleeding in. The unit tests missed it because `IInteractionModelRedeployer` is mocked at the controller level (`UserSkillApiTests`); the redeployer's own poll-and-report logic has NO direct test coverage.

Root cause of the coverage gap: `RedeployAsync` orchestrates over `user.SmapiManagement` (a concrete class whose `GetSkillStatusAsync`/`UpdateSkillAsync` are non-virtual), so its logic can't be exercised without either (a) making those methods virtual or (b) extracting an `ISmapiManagement` interface and injecting it.

Task: introduce the seam (interface or virtual methods) and add unit tests for `RedeployAsync` covering: (1) scoped rebuild reports only the deployed locale; (2) all-locales rebuild reports all locales; (3) a stale IN_PROGRESS/failure on a non-deployed locale does NOT pollute a scoped rebuild's success result (the exact regression this task stems from).

Fixed (live-verified) in commit 0b1b7ef; this task is the preventive test layer so it can't recur silently.
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
