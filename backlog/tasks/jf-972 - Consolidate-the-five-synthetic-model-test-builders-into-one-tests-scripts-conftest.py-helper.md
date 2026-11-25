---
id: JF-972
title: >-
  Consolidate the five synthetic-model test builders into one
  tests/scripts/conftest.py helper
status: To Do
assignee: []
created_date: '2026-11-25 16:45'
labels:
  - nlu-tooling
  - cleanup
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up filed from the JF-970 /simplify round (reuse reviewer's finding 5, not a blocker for that change): tests/scripts/ now holds FIVE per-file copies of the tiny synthetic-model builder (subsumption _model/_write_and_build at tests/scripts/test_nlu_static_subsumption.py:39,60; foundation _write_model:45; tokens _write_model:26; selection _model:38; carriers _synthetic_model from JF-970). A synthetic-model schema change must be replicated in five places. Fix: one shared builder in tests/scripts/conftest.py (a fixture or plain helper importing build_effective_model), each file's private copy replaced by the shared one. Mechanical, do it opportunistically when a test file is next touched for other reasons or in a quiet batch; do NOT churn all five files in the same commit as unrelated work.
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
