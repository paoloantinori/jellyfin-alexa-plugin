---
id: JF-850
title: >-
  run_warning_phase helper: the five same-shaped warning-phase blocks in
  validate_interaction_models.py (3/5/6/11 + the phase-8 variant) cross the
  extraction-on-convergence threshold
status: In Progress
assignee: []
created_date: '2026-10-09 20:35'
updated_date: '2026-10-09 20:36'
labels:
  - tooling
  - tech-debt
  - cleanup
dependencies: []
references:
  - scripts/validate_interaction_models.py
  - tests/scripts/test_es_trio_mirroring.py
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-09 by the orchestrator from the JF-844.1 /simplify round (the reuse agent's finding, deliberately not taken in-task as a drive-by refactor of four pre-existing phases).

validate_interaction_models.py now has FIVE warning-phase blocks sharing the same shape (build a warnings list behind a phase title, skip with a printed SKIP line when an input is None, never an all-clear over unchecked parity): phases 3, 5, 6, and the new 11, plus a variant in 8. That is past the repo's extraction-on-convergence threshold (hoist at the third caller). Deliverable: a run_warning_phase(title, fn)-shaped helper (or whatever shape fits after reading all five: the variant in phase 8 may need a parameter) owning the skip-print contract once; the five phases migrate onto it with no behavior change (warning texts byte-identical; the None-skip prints identical). Guard: the existing warning-count baseline (291 on the current tree) and the tests/scripts suite (35 tests incl. the 12 es-trio mirroring tests that pin Phase 11's behavior) must stay green with zero output diff on the clean tree.
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
