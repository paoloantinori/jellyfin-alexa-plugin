---
id: JF-199
title: Cache DynamicEntities Build output with short TTL
status: Done
assignee: []
created_date: '2026-05-22 05:28'
updated_date: '2026-05-22 06:02'
labels:
  - performance
  - dynamic-entities
milestone: Performance
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntityBuilder.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntitiesInterceptor.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

`DynamicEntityBuilder.Build()` makes 5+ uncached DB queries per invocation (artists, albums, series, audiobooks, last-played). This runs on every LaunchRequest and new session, and was the cause of the 19.7s timeout seen in production logs (`TaskCanceledException` in `DynamicEntitiesInterceptor`).

JF-167 already covers caching the last-played query specifically. This task covers caching the entire `DynamicEntitiesDirective` output.

## Implementation Plan

### Phase 1: Add in-memory cache to DynamicEntityBuilder

In `DynamicEntityBuilder.cs`, add a `ConcurrentDictionary` keyed by `(userId, locale, includeSeries, includeAudiobooks)` with a `DateTime` expiration:

```csharp
private static readonly ConcurrentDictionary<CacheKey, (DynamicEntitiesDirective Directive, DateTime ExpiresAt)> _cache = new();
private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

private record CacheKey(Guid UserId, string Locale, bool IncludeSeries, bool IncludeAudiobooks);
```

### Phase 2: Check cache before querying

At the top of `Build()`:
1. Check `_cache.TryGetValue(key, out var cached)`
2. If cached and not expired, return cached directive
3. After building, store result in cache

### Phase 3: Invalidate on library changes

Subscribe to `ILibraryManager.ItemAdded` / `ItemRemoved` / `ItemUpdated` events to clear cache entries for affected users. Or simpler: clear all entries on any library change (since the TTL is short).

### Phase 4: Test

- Unit test: second call within TTL returns cached result without DB queries
- Unit test: expired cache entry triggers rebuild
- Unit test: library change event invalidates cache

## Key Files
- `Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntityBuilder.cs`
- `Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntitiesInterceptor.cs`

## Related: JF-167 (cache last-played query specifically — this task supersedes it)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 DynamicEntityBuilder.Build() returns cached result within TTL without DB queries
- [ ] #2 Cache invalidates on library change events
- [ ] #3 Cache invalidates after TTL expiry (2 minutes)
- [ ] #4 Unit tests cover cache hit, miss, and invalidation
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added output-level cache to DynamicEntityBuilder keyed by (userId, locale, includeSeries, includeAudiobooks) with 2-min TTL. Cache invalidated on library changes via ILibraryManager events. Implements IDisposable. Before: 5+ DB queries per Build(). After: 0 queries on cache hit. Committed as b4ba393.
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
