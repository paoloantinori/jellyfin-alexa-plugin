---
id: JF-435
title: >-
  Dry-run e2e leaks real SMAPI calls: autouse session-reset fixture has no
  dry-run guard
status: Done
assignee:
  - zai
created_date: '2026-09-01 10:39'
updated_date: '2026-09-01 21:00'
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
- [x] #1 The autouse _reset_simulation_session fixture (test_e2e.py:41-70) is a no-op in dry-run mode (guard on the same dry-run flag conftest uses), matching the documented 'validate fixtures only, no SMAPI calls' contract
- [x] #2 A dry-run e2e run makes ZERO ask/simulate subprocess invocations (assert or measure)
- [x] #3 Live (non-dry-run) behavior unchanged
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-435: the dry-run contract is enforced again - zero SMAPI subprocess calls, sub-second dry-run.

WHAT CHANGED (commit 47594e8)
- tests/integration/test_e2e.py: the autouse _reset_simulation_session fixture early-returns on dry-run (the same --dry-run option conftest registers); it used to fire a real 'ask smapi simulate-skill stop' before every test's own skip.
- Review follow-up applied: the 9 scattered copies of the dry-run predicate (conftest x4, test_nlu x1, test_e2e x4) collapsed into ONE session-scoped dry_run fixture in conftest.py - 'no SMAPI subprocess in dry-run' is now a declared dependency, not a remembered convention (this was exactly the mechanism that let JF-435 happen).

VERIFICATION
- Dry-run: 56 skipped in 0.24-0.9s (was ~5 min); ZERO subprocess.Popen events, proven with a throwaway audit-hook counter (validated by one deliberate subprocess.run yielding exactly one event; the counter is not committed).
- Official wrapper ./scripts/run_e2e_tests.sh --dry-run: 56 skipped, 1.1s end-to-end.
- Live path unchanged: verified with a PATH-stubbed ask (zero network) - the fixture still issues the reset before each live test.
- Gates: /simplify (implementer, 4 angles, no findings + the shared-fixture skip now DONE by follow-up); code-review high (no correctness findings; the one cleanup finding = the shared fixture, applied). Unit suite 2812/2812 (python-only change; C# untouched).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
