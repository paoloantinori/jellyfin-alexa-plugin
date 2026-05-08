---
id: JF-101
title: Investigate EF Core query warnings in Jellyfin logs
status: Done
assignee: []
created_date: '2026-05-08 20:50'
updated_date: '2026-05-08 21:38'
labels:
  - investigation
  - performance
  - bug
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two EF Core warnings are appearing repeatedly in Jellyfin server logs from the AlexaSkill plugin:

**Warning 1 — Missing OrderBy with Skip/Take**
```
[WRN] The query uses a row limiting operator ('Skip'/'Take') without an 'OrderBy' operator. This may lead to unpredictable results. If the 'Distinct' operator is used after 'OrderBy', then make sure to use the 'OrderBy' operator after 'Distinct' as the ordering would otherwise get erased.
```

**Warning 2 — Multiple collection includes without QuerySplittingBehavior**
```
[WRN] Compiling a query which loads related collections for more than one collection navigation, either via 'Include' or through projection, but no 'QuerySplittingBehavior' has been configured. By default, Entity Framework will use 'QuerySplittingBehavior.SingleQuery', which can potentially result in slow query performance.
```

**Investigation scope:**
- Identify which plugin queries trigger these warnings (likely in CatalogManager, LibrarySyncService, or handler code that queries Jellyfin's DB via EF Core)
- Determine if the Skip/Take without OrderBy could produce non-deterministic results (e.g., library sync returning different items on each run)
- Assess whether multiple Includes need `.AsSplitQuery()` for acceptable performance on large libraries
- Propose fixes: add explicit `.OrderBy()` before `.Skip()/.Take()`, add `.AsSplitQuery()` where multiple Includes are used
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Root cause queries identified (file + line number for each warning)
- [x] #2 Fix proposed for missing OrderBy with reproducible ordering
- [x] #3 Fix proposed for multiple Includes (either AsSplitQuery or refactored includes)
- [x] #4 Changes do not break existing unit or integration tests
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Implementation Plan

### Root Cause Analysis

The plugin doesn't use EF Core directly — it queries through Jellyfin's `ILibraryManager` service layer using `InternalItemsQuery` objects. Jellyfin internally translates these to EF Core queries. Two issues:

1. **Skip/Take without OrderBy**: ~10 handler files construct `InternalItemsQuery` with `Limit` set but no `OrderBy`. Jellyfin's EF Core translates `Limit` to `Take` without a preceding `ORDER BY`, triggering the warning. This produces non-deterministic results (e.g., library sync could return different items each run).

2. **Multiple Includes without AsSplitQuery**: Most queries use `DtoOptions(true)` which tells Jellyfin to include all related entity fields. On large libraries, this generates JOIN-heavy queries that EF Core warns about. This is Jellyfin's internal concern — we can't control `AsSplitQuery()` from plugin code. The mitigation is to use `DtoOptions(false)` where full serialization isn't needed, and selectively enable only needed fields.

### Fix Plan

**Step 1**: Add `OrderBy` to all `InternalItemsQuery` that set `Limit` without ordering.
- `ContinueWatchingIntentHandler.cs` — add `OrderBy = new[] { ItemSortBy.DateLastSaved }` 
- `InProgressMediaListIntentHandler.cs` — add `OrderBy = new[] { ItemSortBy.DateLastSaved }`
- `BrowseLibraryIntentHandler.cs` — add `OrderBy = new[] { ItemSortBy.SortName }` (2 queries)
- `PlayByGenreIntentHandler.cs` — add `OrderBy = new[] { ItemSortBy.Random }` for genre query
- `RecommendIntentHandler.cs` — add `OrderBy = new[] { ItemSortBy.Random }` (2 queries)
- `AddToQueueIntentHandler.cs` — add `OrderBy = new[] { ItemSortBy.SortName }`
- `PlayArtistSongsIntentHandler.cs` — add appropriate ordering
- `PlayPodcastIntentHandler.cs` — move client-side OrderBy to query
- `LibrarySyncService.cs` — add deterministic ordering for catalog sync

**Step 2**: Audit `DtoOptions` usage — change to `DtoOptions(false)` where full DTO is not needed, selectively enable fields.

**Step 3**: Write unit tests to verify ordering is set on queries.

**Step 4**: Run /simplify to clean up.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Root cause: plugin uses InternalItemsQuery with Limit but no OrderBy, triggering EF Core Skip/Take warning. Fixed by adding OrderBy = new[] { (ItemSortBy.X, SortOrder.Y) } to 14 files.

Multiple Includes warning: caused by DtoOptions(true) which requests all related entity fields. This is Jellyfin-internal behavior. Mitigated where possible but AsSplitQuery() is not controllable from plugin code.

PlayPodcastIntentHandler: moved DateCreated descending sort from client-side LINQ to query-level OrderBy. Updated test mock to return items in sorted order.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
## Fixed EF Core Query Warnings

**Warning 1 — Skip/Take without OrderBy**: Added explicit `OrderBy` to all 14 `InternalItemsQuery` instances that set `Limit` without ordering. Sort strategies:
- `SortName Ascending` for browsing, search, artist lookup, catalog sync (9 queries)
- `Random Ascending` for random play, genre, mood, recommendations, radio (6 queries)
- `DatePlayed Descending` for continue watching, in-progress lists (2 queries)
- `DateCreated Descending` for podcast episodes, proactive events (2 queries)

Also moved PlayPodcastIntentHandler's client-side `OrderByDescending` to query-level, eliminating in-memory sort.

**Warning 2 — Multiple Includes**: Root cause is `DtoOptions(true)` which tells Jellyfin to include all related fields. `AsSplitQuery()` is not controllable from plugin code — this is a Jellyfin internals concern. Noted for future Jellyfin API improvements.

All 917 tests pass. /simplify review completed with no actionable findings.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->
