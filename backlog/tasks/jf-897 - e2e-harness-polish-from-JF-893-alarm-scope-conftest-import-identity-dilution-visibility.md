---
id: JF-897
title: >-
  e2e harness polish from JF-893: alarm scope, conftest import identity,
  dilution visibility
status: To Do
assignee: []
created_date: '2026-10-16 10:34'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Residuals from the JF-893 review round (worker handback, 2026-10-16), one task for the next harness touch: (1) the per-test SMAPI alarm (conftest _timeout_handler) guards only the simulate call, so a fire during session reset/delay/summary still FAILs despite proven-correct iterations; (2) 'from conftest import _TimeoutError' assumes pytest resolves conftest to a single module instance; a future rootdir/sys.path change would silently break the except match (an assert on module identity or moving the exception to a shared module would harden it); (3) a slow-service run silently shrinks the N-iteration battery to 1 iteration (the skip reason records '1/5' so it stays visible; a possible improvement is recording the dilution in the session summary line too).
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
