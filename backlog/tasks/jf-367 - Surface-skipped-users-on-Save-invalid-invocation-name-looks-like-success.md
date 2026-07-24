---
id: JF-367
title: Surface skipped users on Save (invalid invocation name looks like success)
status: Done
assignee: []
created_date: '2026-07-24 19:32'
updated_date: '2026-07-24 19:39'
labels:
  - ux
  - config
  - bug
  - JF-365
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
In the save loop (config.html saveConfigButton handler), if a row's invocation name fails `isValidInvocationName`, the code does `validateInvocationName(...); continue;` — silently skipping that user's entire save. The inline field error shows, but JF-365 hides the invocation field inside a collapsed panel by default, so the user likely never sees the error. The Save spinner clears and looks successful while one user's changes are silently dropped.

FIX: when one or more rows are skipped, surface a clear aggregate alert after the batch, e.g. `Dashboard.alert("N user(s) not saved: invocation name must be 2+ words. Expand the row to fix.")`. Track a skip count in the loop and fire the alert in `.finally()` or after Promise.all. Keep the per-row inline validation as-is.

This is a JF-365-introduced observability regression (the field was always visible before). Verify on minix: set an invalid 1-word invocation on a user, click Save, confirm the alert appears and names the problem.
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
