---
id: JF-369
title: Guard the unguarded invocationInput read in the save loop
status: Done
assignee: []
created_date: '2026-07-24 19:32'
updated_date: '2026-07-24 19:39'
labels:
  - config
  - robustness
  - JF-365
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
In the save loop (config.html), every per-user field is read through the `field()` helper with a null-fallback ternary, EXCEPT the invocation input: `const invocationInput = details.querySelector('[data-field="InvocationName"]'); if (!isValidInvocationName(invocationInput.value))`. If that element is ever missing (DOM corruption, a future refactor, an emby component upgrade that reparents the input), `invocationInput.value` throws `Cannot read properties of null (reading 'value')` and aborts the ENTIRE save loop — no users saved, no error shown. We saw emby component-upgrade crashes this session, so "DOM differs from expected" is not hypothetical.

FIX: null-guard the invocationInput read. If missing, skip that row (treat like the validation-skip path, ideally counting toward the skipped alert from the sibling task) rather than throwing. Minimal defensive guard, no behavior change in the happy path.
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
