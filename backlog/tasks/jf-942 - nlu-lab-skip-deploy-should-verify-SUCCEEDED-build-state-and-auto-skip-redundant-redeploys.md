---
id: JF-942
title: >-
  nlu-lab: --skip-deploy should verify SUCCEEDED build state and auto-skip
  redundant redeploys
status: To Do
assignee: []
created_date: '2026-11-22 07:06'
labels: []
dependencies: []
references:
  - scripts/nlu-lab/cli.py
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Fable framework review (2026-11-22): --skip-deploy profiles whatever is deployed without checking the build is in a good state. Also, redeploying an already-deployed model mints no new eTag and burns the full 600s timeout before failing loud; the stock baseline is the most natural first command for a new user, so this will bite. Auto-skip detection + build status check.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 --skip-deploy reads the current build entry and refuses (or warns loudly) if the status is not SUCCEEDED
- [ ] #2 Auto-detect: if the model file's sha matches the sha recorded in the most recent successful run against the same skill, print a hint suggesting --skip-deploy instead of timing out for 600 seconds
- [ ] #3 Unit tests for both behaviors
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
