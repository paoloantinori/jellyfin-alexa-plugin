---
id: JF-127
title: Locale fallback chain for missing translations
status: Done
assignee: []
created_date: '2026-05-12 04:45'
updated_date: '2026-05-12 12:12'
labels:
  - enhancement
  - localization
  - reliability
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement a locale fallback chain: try exact locale first (e.g., es-MX), then fall back to the language root (es), then to en-US as ultimate fallback. Currently missing locale keys cause runtime exceptions.

Inspired by AskPlex's language interceptor that tries specific locale then falls back to generic language. Their contributor giogua added this to make the skill work across locale variants without needing complete translations for every one.

Implementation: Modify `ResponseStrings.Get()` to implement a fallback chain before throwing. Log warnings for missing keys at the specific locale level.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Locale lookup tries exact match first (e.g., es-MX), then falls back to language root (es), then en-US
- [ ] #2 No runtime exceptions for missing locale keys
- [ ] #3 Fallback chain logged at debug level for troubleshooting
- [ ] #4 All existing locale files verified to have complete coverage
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
ResponseStrings.Get() now has 4-step fallback: exact locale → language root → en-US → key itself. Never throws. 6 new tests. Build clean, 1087 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->

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
