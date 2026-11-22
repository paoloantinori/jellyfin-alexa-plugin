---
id: JF-941
title: >-
  nlu-lab: bracket the battery with a post-run eTag check (detect mid-battery
  redeploy contamination)
status: Done
assignee: []
created_date: '2026-11-22 07:06'
updated_date: '2026-11-22 07:22'
labels: []
dependencies: []
references:
  - scripts/nlu-lab/cli.py
  - scripts/nlu-lab/smapi_ops.py
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Fable framework review (2026-11-22): nothing brackets the battery. A second same-locale run, or the plugin's own rebuild endpoint, can redeploy mid-battery and the run file will mix routing results from two different models without any indication. The fix is cheap: re-read the eTag after the last row and mark the run tainted if it moved.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 After the last battery row, re-read the build eTag and compare it to the one captured at run start; if it changed, mark the run status 'tainted' with a warning (a concurrent redeploy mixed two models in one run)
- [ ] #2 Unit test: fake eTag changes mid-battery, run file carries status=tainted
- [ ] #3 Suite green
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
