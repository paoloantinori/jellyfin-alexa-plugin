---
id: JF-95
title: Add IScheduledTask for periodic token refresh and cache cleanup
status: Done
assignee: []
created_date: '2026-05-07 06:08'
updated_date: '2026-05-07 11:20'
labels: []
dependencies:
  - JF-94
references:
  - claudedocs/research_jellyfin_plugin_best_practices_2026-05-07.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Context
The LDAP Auth and Trakt plugins implement `IScheduledTask` for background operations that appear in the Jellyfin dashboard under Scheduled Tasks. Our plugin currently only refreshes LWA tokens at startup (in `SkillStartup`). If a token expires while the server is running, the skill becomes non-functional until restart. Similarly, `SearchResultCache` grows unbounded over long uptimes.

## What
Implement `IScheduledTask` (from `MediaBrowser.Model.Tasks`) for two periodic operations:

### 1. Token Refresh Task
- Runs periodically (default: every 6 hours, configurable)
- Iterates all configured users
- For each user with a refresh token, proactively refreshes the LWA access token before it expires
- Updates `User.SmapiDeviceToken` and persists via `Plugin.Instance.SaveConfiguration()`
- Logs refreshed/expired/failed users
- Visible in Jellyfin dashboard as "Alexa Skill - Refresh LWA Tokens"

### 2. Cache Cleanup Task
- Runs periodically (default: hourly)
- Removes expired entries from `SearchResultCache`
- Logs cache size before/after cleanup
- Visible in Jellyfin dashboard as "Alexa Skill - Cleanup Search Cache"

Both tasks should be registered in `Registrator.cs` and implement `IScheduledTask` with:
- `Name`, `Description`, `Category` properties
- `ExecuteAsync()` with progress reporting via `IProgress<double>`
- `GetDefaultTriggers()` returning appropriate interval triggers

## Why
Reliability — prevents token expiry gaps during long uptimes. Memory — prevents unbounded cache growth. Visibility — admins see tasks in the Jellyfin dashboard and can run them manually.

## Key Files
- New: `EntryPoints/TokenRefreshTask.cs` — implements `IScheduledTask`
- New: `EntryPoints/CacheCleanupTask.cs` — implements `IScheduledTask`
- `EntryPoints/Registrator.cs` — register new services
- `Alexa/Cache/SearchResultCache.cs` — add `RemoveExpired()` method
- `Lwa/LwaClient.cs` — already has `RefreshDeviceToken()`, reuse it
- `Entities/User.cs` — token state already tracked here

## Reference
- LDAP Auth pattern: `LdapProfileImageSyncTask : IScheduledTask`
- Trakt pattern: `SyncFromTraktTask`, `SyncLibraryTask`
- Research report: `claudedocs/research_jellyfin_plugin_best_practices_2026-05-07.md` (Recommendation #2)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 TokenRefreshTask implements IScheduledTask with Name, Description, Category properties
- [ ] #2 TokenRefreshTask.ExecuteAsync refreshes LWA tokens for all users with refresh tokens
- [ ] #3 TokenRefreshTask persists updated tokens via SaveConfiguration()
- [ ] #4 TokenRefreshTask logs results: refreshed count, failed count, skipped count
- [ ] #5 CacheCleanupTask implements IScheduledTask with Name, Description, Category properties
- [ ] #6 SearchResultCache gets RemoveExpired() method that purges entries past TTL
- [ ] #7 CacheCleanupTask calls RemoveExpired() and logs before/after cache size
- [ ] #8 Both tasks registered in Registrator.cs and discoverable by Jellyfin
- [ ] #9 GetDefaultTriggers() returns sensible defaults (6h for tokens, 1h for cache)
- [ ] #10 Tasks appear in Jellyfin dashboard under Scheduled Tasks and can be run manually
- [ ] #11 Unit tests for token refresh logic (mock LwaClient, verify SaveConfiguration called)
- [ ] #12 Unit tests for cache cleanup logic (verify expired entries removed, valid entries kept)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Starting implementation of IScheduledTask for token refresh and cache cleanup.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented two IScheduledTask implementations visible in the Jellyfin dashboard:

- **TokenRefreshTask** (`EntryPoints/TokenRefreshTask.cs`): Refreshes LWA OAuth tokens for all configured users on a 6-hour interval. Iterates users, calls `LwaClient.RefreshDeviceToken()`, and saves config if any tokens were refreshed or failed.

- **CacheCleanupTask** (`EntryPoints/CacheCleanupTask.cs`): Removes expired entries from `SearchResultCache` on a 1-hour interval.

- **SearchResultCache.RemoveExpired()** (`Alexa/Cache/SearchResultCache.cs`): Thread-safe two-pass cleanup (collect expired keys, then remove) for `ConcurrentDictionary`. Also added `NoopSearchResultCache.RemoveExpired()` override.

Both tasks registered as singletons in `Registrator.cs`. 13 unit tests covering expiration logic, task metadata, triggers, and execution behavior.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->
