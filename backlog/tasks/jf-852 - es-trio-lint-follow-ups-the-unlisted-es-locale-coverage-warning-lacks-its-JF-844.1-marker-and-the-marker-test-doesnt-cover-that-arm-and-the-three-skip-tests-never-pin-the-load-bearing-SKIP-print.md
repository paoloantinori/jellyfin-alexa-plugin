---
id: JF-852
title: >-
  es-trio lint follow-ups: the unlisted-es-locale coverage warning lacks its
  JF-844.1 marker (and the marker test doesn't cover that arm), and the three
  skip tests never pin the load-bearing SKIP print
status: To Do
assignee: []
created_date: '2026-10-09 21:15'
labels:
  - tooling
  - test-coverage
dependencies: []
references:
  - scripts/validate_interaction_models.py
  - tests/scripts/test_es_trio_mirroring.py
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-09 by the orchestrator from the JF-850 /code-review high round (two findings on the JUST-COMMITTED JF-844.1 surface, delivered for same-turn tracker landing; both small, same area).

(1) MARKER-COVERAGE GAP (validate_interaction_models.py, the Phase 11 lint): the unlisted-es-locale coverage warning (the F2-applied arm, ~line 807 area) lacks the "(JF-844.1)" marker its own marker-coverage convention promises, and the marker-coverage TEST only exercises the sibling-row shape, so the marker's absence on the coverage arm is unguarded. Fix: add the marker to the coverage warning string and extend the marker test to the coverage-arm shape.

(2) SKIP-PRINT PINS (tests/scripts/test_es_trio_mirroring.py): the three es-trio skip tests assert only `is None` and never pin the SKIP print the lint's own contract calls load-bearing (the JF-612 rule: never an all-clear over unchecked parity - a SKIP print is the honest signal). Fix: capsys assertions pinning the exact SKIP line per skip shape.
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
