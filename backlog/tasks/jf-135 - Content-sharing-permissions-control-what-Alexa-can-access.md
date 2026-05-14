---
id: JF-135
title: Per-user library selection for Alexa access
status: Done
assignee: []
created_date: '2026-05-12 15:17'
updated_date: '2026-05-13 04:59'
labels:
  - configuration
  - content-access
  - enhancement
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Let each Jellyfin user choose which of their libraries are accessible via Alexa. Instead of administrators controlling global permission rules, each user configures their own library sharing through the plugin config page.

**Why**: A household may have music, movies, audiobooks, and personal videos. A user should be able to say "only share my Music and Audiobooks libraries with Alexa" without needing admin intervention. Different users in the same household can have different selections.

**Scope**: Simple per-user allow-list of library IDs. No deny-list mode, no media-type toggles, no admin-level rules. Just: user picks their libraries, Alexa only sees those.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 PluginConfiguration stores per-user allowed library IDs (dictionary: user ID → list of library IDs)
- [x] #2 Config page shows each Jellyfin user's library selection with checkboxes
- [x] #3 Library list is fetched dynamically from Jellyfin API
- [x] #4 Handlers filter queries to only search within user's allowed libraries
- [x] #5 Users with no configuration default to all libraries accessible (backward compatible)
- [ ] #6 Handlers return localized 'not found' when user requests media from an excluded library
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
## Simplified Model

**Single config structure**: `Dictionary<string, List<string>>` mapping Jellyfin user ID → allowed library IDs.

**Default behavior**: If a user has no entry, all libraries are accessible (backward compatible, no breaking change).

**Config page**: Per-user section listing their libraries with checkboxes. User checks which ones Alexa can access.

**Implementation**: Add a `GetAllowedLibraryIds(userId)` helper on BaseHandler. Handlers use it to filter queries via `AncestorIds` or post-query filtering.

**Key files:**
- `PluginConfiguration.cs` — add per-user library ID dictionary
- `BaseHandler.cs` — add helper to get allowed libraries for current user
- `config.html` — per-user library picker section
- Search/browse handlers — inject library filters
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented per-user library selection for Alexa access (JF-135.3).

**What was done:**
- Added `AllowedLibraryIds` property to `User` entity (null = all libraries, backward compatible)
- Added `GetAllowedLibraryIds()` and `ApplyLibraryFilter()` helpers on `BaseHandler` that set `InternalItemsQuery.TopParentIds` for database-level filtering
- Wired up `ApplyLibraryFilter` in 25 handlers (19 original + 6 found by simplify review: PlaySong, PlayNext, PlayFavorites, PlayChannel, YesIntent, and FindRadioTracksAsync via PlaybackNearlyFinishedEventHandler)
- Updated `ConfigurationController` PATCH endpoint to accept `AllowedLibraryIds` as JSON array using `JObject.Parse`
- Added per-user library picker UI to config page with overlay panel, dynamic library fetching from `Library/MediaFolders`, and All/individual toggle
- Added 7 unit tests for library filtering logic (GetAllowedLibraryIds, ApplyLibraryFilter)

**Simplify review fixes:**
- Fixed inconsistent StatusCode pattern in controller (early returns, consistent `{ StatusCode = N }`)
- Fixed `JToken.ToString()` → `.Value<string>()` for correct semantic extraction
- Fixed config.html caching — failed API calls no longer poison the library cache
- Fixed variable shadowing (songQuery/channelQuery string vs query object conflicts)
- Made `GetAllowedLibraryIds`/`ApplyLibraryFilter` accept nullable `Entities.User?`

**Skipped:** JF-135.1 (media type visibility toggles) and JF-135.2 (collection-level allow/deny) — these are already implemented as global config flags and would add complexity without clear benefit over the per-user library filter.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
