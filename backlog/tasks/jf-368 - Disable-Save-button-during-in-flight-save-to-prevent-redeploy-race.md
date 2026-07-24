---
id: JF-368
title: Disable Save button during in-flight save to prevent redeploy race
status: Done
assignee: []
created_date: '2026-07-24 19:32'
updated_date: '2026-07-24 19:39'
labels:
  - ux
  - config
  - bug
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The Save button is not disabled during the in-flight save. The invocation-name change path triggers a ~15-30s Amazon redeploy (all 17 locales). An impatient second click fires a second full batch of PATCHes plus a second concurrent all-locales redeploy on the same skill — risking SMAPI throttle/rebuild conflicts where the second overwrites the first's in-progress build.

FIX: disable `#saveConfigButton` at the start of the handler, re-enable in `.finally()`. Standard idempotency guard. Pair with the visual loading state that already exists. Verify on minix: click Save (with an invocation-name change so the redeploy runs), confirm a second click is blocked until the redeploy completes.

Pre-existing, but the redeploy duration (introduced JF-297) makes it materially more likely to bite.
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
