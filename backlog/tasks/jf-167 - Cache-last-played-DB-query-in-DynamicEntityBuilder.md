---
id: JF-167
title: Cache last-played DB query in DynamicEntityBuilder
status: Done
assignee:
  - agent-jf167
created_date: '2026-05-17 11:20'
updated_date: '2026-05-17 12:28'
labels:
  - performance
  - optimization
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntityBuilder.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`BuildLastPlayedValues` runs a full `GetItemList` query with `OrderBy DatePlayed Descending` on every `Build()` call — both on new sessions and mid-session TV/book intents. Since `DynamicEntityBuilder` is a singleton, it could hold a per-user cache with a short TTL (5-10 min) to eliminate the DB round-trip on most requests.

The pattern already exists in the codebase: `ArtistIndexService` maintains an in-memory cache with event-driven refresh. A similar approach (or a simple `ConcurrentDictionary<Guid, (List, DateTime)>` with TTL) would work here.

Low priority until profiling shows this query is a bottleneck.
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
Added a per-user ConcurrentDictionary cache to DynamicEntityBuilder for last-played DB queries. Cache key is user GUID, value is (List, DateTime) with 5-minute TTL. On cache hit, returns cached values without DB round-trip. On miss, queries DB and stores result. Budget accounting works correctly in both paths. All 1516 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
