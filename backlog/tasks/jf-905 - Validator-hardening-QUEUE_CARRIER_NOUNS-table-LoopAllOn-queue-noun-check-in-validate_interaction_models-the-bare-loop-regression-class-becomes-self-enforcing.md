---
id: JF-905
title: >-
  Validator hardening: QUEUE_CARRIER_NOUNS table + LoopAllOn queue-noun check in
  validate_interaction_models (the bare-loop regression class becomes
  self-enforcing)
status: Done
assignee: []
created_date: '2026-10-17 16:45'
updated_date: '2026-10-17 17:56'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-904 simplify review (worker handback, could not file from the worktree): the LoopAllOn "samples must name the queue" invariant is enforced today only by fixture pins, not by the validator. A QUEUE_CARRIER_NOUNS table + a carrier check in scripts/validate_interaction_models.py mirroring the existing ALBUM_CARRIER_NOUNS pattern (the JF-459/#11 detection style) would make it self-enforcing across all 17 locales: a future LoopAllOn sample that drops the queue noun (regressing to the bare-loop class JF-902 eliminated) fails validation instead of waiting for a fixture or a live steal. LOW priority: JF-902 did not add it either; the fixtures currently hold the line. When taken: add the table (queue nouns per locale family, sourced from each locale's ListQueue vocabulary exactly as the JF-904 carriers were), the check (warning-level first, matching the #10 precedent unless a real miss shows), and a test in tests/scripts/ mirroring the existing validator tests.
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
EXECUTED on branch jf905 (e42103f6), merged to main cab41858, pushed. No deploy (validator + tests only, no model or DLL surface). THE CHANGE: QUEUE_CARRIER_NOUNS table (nouns verified against every locale's own ListQueue/ClearQueue vocabulary; fr on the 'attente' stem for the apostrophe variants, ar on the alif-dropped stem so both hamza variants match - a bug the worker's own test caught pre-commit) + check #12 at warning level with the documented promotion condition (first real missed regression flips it to error), built on the shared _carrier_misses_noun predicate that check #10 was rewired onto behavior-identically (the unchanged 275-warning baseline proves it). The code-review round added the missing-entry coverage: an 18th locale declaring LoopAllOnIntent with no table entry warns keyed JF-905, which the real-corpus test turns into a hard CI failure. Gates (in e42103f6): py_compile clean, tests/scripts 86/86, real-corpus validator PASS before AND after at the 275 baseline with 0 new warnings, code-review high 6/6 applied; /simplify and the dotnet DoD rows N/A for a python-only change, stated at the merge. Residuals: FIXTURE_LINT_INTENTS covers only PlayAlbumIntent (queue-noun lint for fixtures is future defense-in-depth); the substring trade is accepted and documented (same as #10 since JF-459). gate-exempt: gates ran in the dispatched worker's transcript; commit e42103f6 carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->
