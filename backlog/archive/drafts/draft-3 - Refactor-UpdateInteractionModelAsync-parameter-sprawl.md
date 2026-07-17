---
id: DRAFT-3
title: Refactor UpdateInteractionModelAsync parameter sprawl
status: Draft
assignee: []
created_date: '2026-05-08 20:51'
updated_date: '2026-05-12 11:22'
labels:
  - refactor
  - smapi
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`SmapiManagement.UpdateInteractionModelAsync` has accumulated too many parameters, making it hard to call and maintain. This needs a record type refactor.

**Context:**
- Method has too many positional parameters, reducing readability and call-site clarity
- Adding new parameters requires updating every call site
- A request/options record type would encapsulate the parameters cleanly

**Scope:**
- Create a record type (e.g., `InteractionModelUpdateRequest`) to hold the parameters
- Refactor `UpdateInteractionModelAsync` to accept the record
- Update all call sites to use the new signature
- Verify tests still pass
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
- [ ] #2 dotnet build passes with 0 errors
- [ ] #3 dotnet test passes
- [ ] #4 No new compiler warnings introduced
- [ ] #5 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #6 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #7 NLU test fixtures updated if interaction model changed
- [ ] #8 E2E test added for new intent or handler logic
- [ ] #9 Locale response strings added to all 12 locales
<!-- DOD:END -->
