---
id: JF-200
title: Batch GetUserData to eliminate N+1 queries in InProgressMediaList
status: Done
assignee: []
created_date: '2026-05-22 05:28'
updated_date: '2026-05-22 06:14'
labels:
  - performance
  - database
milestone: Performance
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/InProgressMediaListIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

`InProgressMediaListIntentHandler` (lines 103-117) loops through ~50 candidate items and calls `userDataManager.GetUserData()` individually for each one. This is a classic N+1 pattern: 1 query to get the item list, then 50 individual DB roundtrips for user data.

## Implementation Plan

### Phase 1: Replace loop with batch API

Jellyfin 10.11+ provides `IUserDataManager.GetUserData(Guid userId, List<BaseItem> items)` which returns a dictionary of user data for all items in a single query.

In `InProgressMediaListIntentHandler.cs`, replace:

```csharp
// Before: N+1 pattern
foreach (var item in candidateItems)
{
    var userData = _userDataManager.GetUserData(jellyfinUserId, item);
    // ... filter by userData
}
```

With:

```csharp
// After: single batch query
var userDataDict = _userDataManager.GetUserData(jellyfinUserId, candidateItems);
foreach (var item in candidateItems)
{
    var userData = userDataDict.GetValueOrDefault(item.Id);
    // ... filter by userData
}
```

### Phase 2: Check for similar patterns in other handlers

Search for other `GetUserData` calls in loops:
- `BaseHandler.cs` — `FavoritesAndRatingsFirst` method (lines 57-103) loops items calling GetUserData
- `BaseHandler.cs` — `SortAndFindResumeIndex` method (lines 135-138) same pattern

Apply the same batch pattern to all of them.

### Phase 3: Test

- Unit test: verify same filtering results with batch vs individual calls
- Verify no behavioral change in InProgressMediaList output

## Key Files
- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/InProgressMediaListIntentHandler.cs` (lines 103-117)
- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs` (lines 57-103, 135-138)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 InProgressMediaListIntentHandler uses single batch GetUserData call
- [ ] #2 BaseHandler.FavoritesAndRatingsFirst uses batch GetUserData
- [ ] #3 BaseHandler.SortAndFindResumeIndex uses batch GetUserData
- [ ] #4 No behavioral change in filtered/sorted results
- [ ] #5 Unit tests pass
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
APPROACH REVISION: Jellyfin 10.11.8 IUserDataManager has NO batch GetUserData(User, List<BaseItem>) overload. Only GetUserData(User, BaseItem) exists. Revised approach: (1) Add IsPlayed=false to InternalItemsQuery to reduce candidates at DB level, (2) Pre-fetch user data into local dictionary for multi-method callers like FavoritesAndRatingsFirst+SortAndFindResumeIndex
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added IsPlayed=false to InternalItemsQuery in InProgressMediaListHandler and BaseHandler.FindLastPlayedItemWithProgress. Filters fully-played items at DB level instead of fetching and discarding in loop. Before: ~50 GetUserData calls per request. After: only unplayed items returned from DB. Committed as e5ac2d8.
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
