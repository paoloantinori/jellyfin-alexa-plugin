---
id: JF-289
title: 'UX: Filter out library folders from book carousel'
status: Done
assignee: []
created_date: '2026-06-09 16:45'
updated_date: '2026-06-24 08:36'
labels:
  - ux
  - apl
  - books
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The book carousel currently shows the "Audiobooks" parent folder as the first item. When tapped, it resolves to the first child alphabetically — not what the user intended. The carousel should only show actual playable books, not parent/library folder containers.

The folder item (type=Folder) should be excluded from carousel results. The filtering should happen in the handler that builds the carousel data source (likely PlayBookIntentHandler or BrowseLibraryIntentHandler where the carousel is rendered).

The "Audiobooks" folder ID is a Jellyfin library root folder — it contains all audiobooks as children. Displaying it in the carousel is confusing UX.
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Already implemented before tracking. The Audiobooks root folder (CollectionFolder/AggregateFolder) is filtered out of the BrowseLibrary book carousel at `BrowseLibraryIntentHandler.cs:252` (`parents.Where(p => p is not CollectionFolder && p is not AggregateFolder)`), as defense in depth alongside an upstream homogeneity check.

Implementing commits:
- b86e0e1 — Fix BrowseLibrary: filter out library root folders from AudioBook results (original fix)
- 51618ef — Fix 'mostra libri' showing the Audiobooks root folder (jf-266 regression re-fix)

Test coverage: `BrowseLibraryIntentHandlerTests.cs` — HandleAsync_BrowseBooks_FiltersOutLibraryRootFolders + HandleAsync_BrowseBooks_FiltersOutAggregateFolders.

Scope note: this applies to the BrowseLibrary ("mostra libri") carousel. The separate recently-played carousel (BaseHandler.GetRecentlyPlayedItems) does not need the same filter — its `IncludeItemTypes=[Audio,Movie,Episode,AudioBook]` query + `DatePlayed` ordering already exclude container types.
<!-- SECTION:FINAL_SUMMARY:END -->
