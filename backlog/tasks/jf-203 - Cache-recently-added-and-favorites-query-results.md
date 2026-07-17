---
id: JF-203
title: Cache recently-added and favorites query results
status: Done
assignee: []
created_date: '2026-05-22 05:28'
updated_date: '2026-05-22 06:14'
labels:
  - performance
  - cache
milestone: Performance
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/QueryRecentlyAddedIntentHandler.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayFavoritesIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Cache/SearchResultCache.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

`QueryRecentlyAddedIntentHandler` and `PlayFavoritesIntentHandler` query the database on every request without caching. Both return data that rarely changes between requests — recently added items change when the library is updated, favorites change when the user toggles them.

## Implementation Plan

### Phase 1: Add cache entries to SearchResultCache

Extend `SearchResultCache.cs` (which already has `CachedSearchAsync`) with two new methods:

```csharp
public async Task<IReadOnlyList<BaseItem>> GetRecentlyAddedCachedAsync(
    Guid userId, string locale, Func<Task<IReadOnlyList<BaseItem>>> queryFunc)

public async Task<IReadOnlyList<BaseItem>> GetFavoritesCachedAsync(
    Guid userId, string locale, Func<Task<IReadOnlyList<BaseItem>>> queryFunc)
```

Use a 2-minute TTL for recently added, 5-minute TTL for favorites (even less frequently changed).

### Phase 2: Wire into handlers

Replace direct DB queries in both handlers with cache-backed calls:

```csharp
var items = await _cache.GetRecentlyAddedCachedAsync(userId, locale, () =>
    GetRecentlyAddedItems(userId, allowedLibraryIds));
```

### Phase 3: Invalidate on library/user data changes

- Recently added: invalidate on `ILibraryManager.ItemAdded` events
- Favorites: invalidate on `IUserDataManager.UserDataSaved` events (when the key is "liked")

### Phase 4: Test

- Unit test: second call within TTL returns cached result
- Unit test: expired cache triggers re-query
- Unit test: library change invalidates recently-added cache

## Key Files
- `Jellyfin.Plugin.AlexaSkill/Alexa/Cache/SearchResultCache.cs`
- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/QueryRecentlyAddedIntentHandler.cs`
- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayFavoritesIntentHandler.cs`
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Recently-added results cached with 2-min TTL
- [ ] #2 Favorites results cached with 5-min TTL
- [ ] #3 Cache invalidates on library/user-data changes
- [ ] #4 Existing handler behavior unchanged
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Extended SearchResultCache with GetRecentlyAddedCachedAsync (2-min TTL) and GetFavoritesCachedAsync (5-min TTL). Wired into QueryRecentlyAddedIntentHandler and PlayFavoritesIntentHandler. Before: DB query every request. After: cached within TTL. Committed as 0699b87.
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
