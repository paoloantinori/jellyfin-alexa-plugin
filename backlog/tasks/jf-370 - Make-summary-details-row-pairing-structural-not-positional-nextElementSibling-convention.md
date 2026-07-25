---
id: JF-370
title: >-
  Make summary/details row pairing structural, not positional
  (nextElementSibling convention)
status: Done
assignee: []
created_date: '2026-07-24 19:32'
updated_date: '2026-07-25 05:13'
labels:
  - config
  - robustness
  - JF-365
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The row-expand design relies on the details row being the summary row's IMMEDIATE next sibling — both the expand/collapse toggle (`summary.nextElementSibling`) and the save loop (`row.nextElementSibling`) assume it. Nothing structurally enforces this; it's a convention held only by createUserRow always emitting [summary, details] as adjacent siblings. If anything ever inserts a node between them (a future bulk-action row, emby DOM manipulation, a reordered append), the toggle hides the wrong row and the save reads the wrong panel — silently, with no error.

FIX options (pick one): (a) pair them with explicit attributes — summary gets `data-row-id`, details gets `data-details-for="<row-id>"`, and lookups use `tbody.querySelector('tr[data-details-for="X"]')` instead of nextElementSibling; or (b) wrap each user's two rows and query within the wrapper. (a) is the smaller change. Defensive hardening, not a known live bug today.
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
