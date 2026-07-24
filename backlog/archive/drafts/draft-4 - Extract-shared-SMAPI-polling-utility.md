---
id: DRAFT-4
title: Extract shared SMAPI polling utility
status: Draft
assignee: []
created_date: '2026-05-08 20:51'
updated_date: '2026-05-12 11:22'
labels:
  - refactor
  - smapi
  - tech-debt
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
SMAPI polling logic (wait for model build, check skill status, etc.) is duplicated across several call sites. Extract into a shared utility.

**Context:**
- Multiple places poll SMAPI for build/status completion with similar delay/retry loops
- Polling delay, timeout, and retry logic should be centralized
- Would improve consistency and make it easier to adjust polling behavior globally

**Scope:**
- Identify all SMAPI polling patterns (model build status, skill status, etc.)
- Design a reusable polling utility (configurable delay, timeout, cancellation)
- Extract and consolidate polling logic
- Update call sites to use shared utility
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
