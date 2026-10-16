---
id: JF-897
title: >-
  e2e harness polish from JF-893: alarm scope, conftest import identity,
  dilution visibility
status: Done
assignee: []
created_date: '2026-10-16 10:34'
updated_date: '2026-10-16 11:20'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf897 (bf405a0c), merged to main add4fd44, pushed. ALL THREE ITEMS: (1) ALARM SCOPE: the whole reliability loop + summary is wrapped in an outer except that runs the same JF-893 decision as the inner simulate-call catch, via the pure decide_reliability_timeout_exit verdict tuple and a shared _exit_reliability_on_timeout actor; landing alarms during stop_playback/session-reset re-raise instead of being swallowed by the broad handlers, and the inner reraise verdict uses a sentinel so the outer handler does not re-decide under a 'post-loop' label; functional rows keep the strict raw FAIL. (2) IMPORT IDENTITY: _TimeoutError and the handler moved to the shared smapi_timeouts.py module; conftest and test_e2e import the same class object regardless of conftest module identity. (3) DILUTION VISIBILITY: the RELIABILITY SUMMARY line carries the correctly-resolved count and a timeout-skip logs a DILUTED warning with the skip reason's correct/total. Gates (in bf405a0c, adapted for Python-only): py_compile clean 4 files, 10/10 reliability unit tests (5 new verdict-path tests), 13/13 neighboring unit suites, dry-run green, code-review high 6 findings (4 applied: the alarm-swallow re-raise guards, the reraise sentinel, the utterance in the fail message, the new module explicitly staged; 2 dispositioned: post-timeout reset kept for JF-893 parity bounded by the client's HTTP timeout, underscore names kept as suite-internal convention). /simplify and dotnet gates skipped as not applicable, stated at the merge. Residuals: the smoke/full-chain broad handlers can still swallow an alarm (bounded: one lost test, freshly armed per test) and a dilution skip pays one ~10s reset before pytest.skip; both documented. gate-exempt: gates ran in the dispatched worker's transcript; commit bf405a0c carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->
