---
id: DRAFT-5.1
title: 'JF-139-B: Integration tests for per-user library filtering'
status: Done
assignee: []
created_date: '2026-05-13 14:05'
updated_date: '2026-05-13 14:32'
labels:
  - testing
  - config
dependencies: []
references:
  - DRAFT-6
parent_task_id: DRAFT-5
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Subtask of JF-139. See parent for full context.

Write integration-level tests that verify per-user AllowedLibraryIds are honored end-to-end: in intent handlers, catalog sync, and dynamic entity building.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Tests verify that a handler query with AllowedLibraryIds set receives TopParentIds filter
- [ ] #2 Tests verify that catalog sync excludes items from non-allowed libraries
- [ ] #3 Tests verify that dynamic entity building excludes items from non-allowed libraries
- [ ] #4 Tests cover null/empty AllowedLibraryIds (unrestricted) path
- [ ] #5 All existing tests continue to pass
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Extracted LibraryFilter utility to Alexa/Util/LibraryFilter.cs

LibrarySyncService now filters by AllowedLibraryIds before catalog sync

DynamicEntityBuilder accepts optional allowedLibraryIds parameter, applies TopParentIds filter

DynamicEntitiesInterceptor resolves plugin user and passes AllowedLibraryIds to builder

Fixed namespace shadowing: Alexa.Util namespace blocked resolution to root Util class

Updated existing DynamicEntitiesInterceptorTests for new method signatures

13 new integration tests across handlers, catalog, and dynamic entities - all 1332 tests pass

Created 13 integration tests across 3 test files:

Handler/LibraryFilterIntegrationTests.cs - 4 tests (PlaySong + SearchMedia with allowed/null/empty libraries)

Catalog/LibrarySyncServiceTests.cs - 5 tests (catalog sync filtering + SMAPI token/vendor guard tests)

DynamicEntities/DynamicEntityBuilderTests.cs - 4 tests (builder with allowed/null/empty libraries + original overload compat)

All 1332 tests pass, 0 failures
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
