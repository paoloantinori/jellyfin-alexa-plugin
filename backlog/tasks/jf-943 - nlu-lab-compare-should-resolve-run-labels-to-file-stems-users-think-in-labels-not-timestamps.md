---
id: JF-943
title: >-
  nlu-lab: compare should resolve run labels to file stems (users think in
  labels, not timestamps)
status: Done
assignee: []
created_date: '2026-11-22 07:07'
updated_date: '2026-11-22 07:20'
labels: []
dependencies: []
references:
  - scripts/nlu-lab/cli.py
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Fable framework review (2026-11-22): compare requires the full timestamped stem (e.g. '20261121-183426-stock-base'), but users think in labels ('stock-base'). The doc examples use bare labels and fail. Label resolution: take the newest run whose label or stem matches the argument.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 compare accepts a label substring and resolves it to the newest run file with that label (falling back to stem matching if no label matches)
- [ ] #2 Unit test: create runs with labels 'stock-aa-1' and 'stock-aa-2', compare by label works
- [ ] #3 Documentation updated to use labels in examples
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
