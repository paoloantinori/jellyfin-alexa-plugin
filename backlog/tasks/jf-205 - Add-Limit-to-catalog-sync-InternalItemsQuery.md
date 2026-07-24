---
id: JF-205
title: Add Limit to catalog sync InternalItemsQuery
status: Done
assignee: []
created_date: '2026-05-22 05:29'
updated_date: '2026-05-22 05:46'
labels:
  - performance
  - database
milestone: Performance
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/LibrarySyncService.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

`LibrarySyncService.cs` (line 172) calls `_libraryManager.GetItemList(query)` without setting `Limit` on the `InternalItemsQuery`. The query returns ALL matching items from the database, then `.Take(MaxCatalogValues)` truncates in-memory. For large libraries, this fetches thousands of items only to discard most of them.

## Implementation Plan

### Phase 1: Move limit into the query

Add `Limit = MaxCatalogValues` to the `InternalItemsQuery` before calling `GetItemList`:

```csharp
var query = new InternalItemsQuery
{
    // ... existing filters ...
    Limit = MaxCatalogValues,  // DB returns only what we need
};
IReadOnlyList<BaseItem> items = _libraryManager.GetItemList(query);
```

Remove the subsequent `.Take(MaxCatalogValues)` since the DB already limits results.

### Phase 2: Verify all sync methods

Check each sync method in `LibrarySyncService` (artists, albums, series, audiobooks) for the same pattern. Apply `Limit` consistently.

### Phase 3: Test

- Verify catalog sync still populates correct number of items
- Verify no items beyond MaxCatalogValues are processed

## Key Files
- `Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/LibrarySyncService.cs`

## Impact
For a library with 10,000 artists and `MaxCatalogValues=5000`, this avoids fetching and materializing 5,000 extra `BaseItem` objects per sync cycle. Reduces DB load and memory churn.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 All LibrarySyncService queries include Limit in InternalItemsQuery
- [ ] #2 No .Take() truncation after GetItemList
- [ ] #3 Catalog sync populates same number of items as before
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added `Limit = MaxCatalogValues` to InternalItemsQuery in SyncCatalogAsync. Removed in-memory `.Take(MaxCatalogValues)`. DB now returns at most 50K rows instead of all items. Test verifies Limit is set on query. All 6 LibrarySyncService tests pass.
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
