---
id: JF-150
title: 'E2E test: Per-user AllowedLibraryIds filtering'
status: Done
assignee: []
created_date: '2026-05-14 12:28'
updated_date: '2026-05-14 13:52'
labels:
  - testing
  - e2e
  - configuration
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create E2E tests that verify the per-user AllowedLibraryIds configuration correctly restricts search and browse results to only the allowed libraries.

When a user has AllowedLibraryIds set:
- SearchMediaIntent results should only include items from allowed libraries
- BrowseLibraryIntent results should only show allowed libraries
- PlayRandomIntent should only pick from allowed libraries
- All content queries should be filtered by TopParentIds

When AllowedLibraryIds is empty/null:
- All libraries should be accessible (no filtering)

Approach:
1. Configure a test user with specific AllowedLibraryIds
2. Run E2E utterances for search, browse, and random play
3. Verify results only contain items from allowed libraries
4. Clear AllowedLibraryIds and verify all libraries are accessible
5. Use it-IT locale

Note: Unit tests already exist for LibraryFilter.ResolveTopParentIds and BaseHandler.GetAllowedLibraryIds. This adds E2E coverage to verify the full pipeline.

References: BaseHandler.cs (ApplyLibraryFilter, GetAllowedLibraryIds), LibraryFilter.cs, User.cs (AllowedLibraryIds)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 E2E fixture tests search results with AllowedLibraryIds restricted
- [ ] #2 E2E fixture tests browse results with AllowedLibraryIds restricted
- [ ] #3 Results only contain items from allowed libraries when configured
- [ ] #4 All libraries accessible when AllowedLibraryIds is empty
- [ ] #5 Tests use it-IT locale
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Created LibraryFilterIntegrationTests.cs with 15 tests. Verifies PlayRandom, SearchMedia, PlayFavorites, PlayLastAdded handlers correctly set TopParentIds on InternalItemsQuery via ApplyLibraryFilter when user has AllowedLibraryIds. Also tests no-op when filter is empty/null and CollectionFolder resolution.
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
