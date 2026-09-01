---
id: JF-435
title: >-
  Dry-run e2e leaks real SMAPI calls: autouse session-reset fixture has no
  dry-run guard
status: To Do
assignee: []
created_date: '2026-09-01 10:39'
labels:
  - code-review
  - test-infrastructure
dependencies: []
references:
  - 'tests/integration/test_e2e.py:68'
  - tests/integration/conftest.py
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Cap-cut finding from the 2026-09-01 JF-420.3 code-review (CONFIRMED, filed per the review-recommendation discipline): tests/integration/test_e2e.py line 68, autouse fixture _reset_simulation_session (lines 41-70) resets the Alexa simulation session before every e2e test with a real reset_client.simulate('stop') subprocess call, with no dry-run guard. conftest.py guarantees 'in dry-run mode we never call SMAPI' and supplies a placeholder skill id, but this autouse fixture fires before the test body's skip: ./scripts/run_e2e_tests.sh --dry-run executes ~56 real ask smapi simulate-skill subprocess invocations against the placeholder skill id, adding minutes of wall time and breaking the dry-run contract that CI and offline workflows rely on.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The autouse _reset_simulation_session fixture (test_e2e.py:41-70) is a no-op in dry-run mode (guard on the same dry-run flag conftest uses), matching the documented 'validate fixtures only, no SMAPI calls' contract
- [ ] #2 A dry-run e2e run makes ZERO ask/simulate subprocess invocations (assert or measure)
- [ ] #3 Live (non-dry-run) behavior unchanged
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
