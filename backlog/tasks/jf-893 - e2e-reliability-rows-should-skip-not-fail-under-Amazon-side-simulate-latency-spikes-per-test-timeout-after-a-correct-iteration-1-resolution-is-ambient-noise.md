---
id: JF-893
title: >-
  e2e reliability rows should skip (not fail) under Amazon-side simulate latency
  spikes: per-test timeout after a correct iteration-1 resolution is ambient
  noise
status: Done
assignee: []
created_date: '2026-10-16 08:57'
updated_date: '2026-10-16 10:35'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Evidence 2026-10-16 ~10:45-10:55: during the fr/es hard-outage window, it-IT simulate still RESOLVED but slowly (22.5s per simulation vs the usual few seconds). The reliability rows (5 identical iterations, e.g. 'riproduci i miei preferiti') then fail with the per-test SMAPI timeout even though routing is CORRECT (iteration 1 resolved to PlayFavoritesIntent and the full-chain e2e row for the same utterance PASSED in the same minutes). The battery's OUTAGE-skipping only engages on hard simulate failures, so an Amazon-side latency spike reddens reliability rows that measure nothing about the skill. SHAPE OF FIX (small, in tests/integration): in the reliability loop, treat a per-test timeout AFTER at least one correctly-resolved iteration as an ambient-noise SKIP with the reason recorded (service slow, N/M iterations resolved), not a FAIL; alternatively scale the per-test budget by the observed iteration-1 latency. Do not touch the functional e2e rows: they keep the strict timeout. Same class as the simulate_outage_per_locale memory: Amazon-side ambient noise must not read as a plugin regression.
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
EXECUTED on branch jf893 (8c0853c1), merged to main 125d14d7, pushed. THE CHANGE: a pure decision function resolve_reliability_timeout_outcome in test_e2e.py; the reliability loop catches conftest's per-test SMAPI timeout, and a timeout after >=1 correctly-resolved iteration with NO recorded failures ends as a recorded-reason SKIP ('simulate service slow: N/M iterations resolved correctly before timeout'), while a timeout with zero correct iterations or recorded failures (intent mismatches, slow failures) still FAILS carrying the evidence; skip path runs the post-loop cleanup (jellyfin stop_playback + session reset) before pytest.skip; functional rows and conftest untouched. Gates (in 8c0853c1, adapted for a Python-only change): py_compile clean, 5/5 unit tests on the decision function (no SMAPI), run_e2e_tests.sh --dry-run green (158 skipped, 0 failed), /code-review high with 3 findings applied (the failures-guard, the skip-path cleanup, and the two covering unit tests); /simplify and the dotnet gates skipped as not applicable to a 109-line Python-only harness diff, stated at the merge. Live verification deferred by design to the next slow/outage window (the fix is FOR that window; the unit tests pin the decision both ways). Residuals filed as JF-897. gate-exempt: gates ran in the dispatched worker's transcript; commit 8c0853c1 carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->
