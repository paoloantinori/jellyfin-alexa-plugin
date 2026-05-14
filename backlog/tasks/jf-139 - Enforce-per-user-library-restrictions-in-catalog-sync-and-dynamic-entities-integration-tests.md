---
id: JF-139
title: >-
  Enforce per-user library restrictions in catalog sync and dynamic entities +
  integration tests
status: Done
assignee: []
created_date: '2026-05-13 14:04'
updated_date: '2026-05-13 14:33'
labels:
  - bug
  - config
  - catalog
  - dynamic-entities
  - testing
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

Per-user library selection (`AllowedLibraryIds`) was added in JF-135, but it is only enforced at the intent handler layer. The two systems that populate Alexa's voice recognition — `LibrarySyncService` (catalog sync) and `DynamicEntityBuilder` (dynamic entities) — ignore per-user library restrictions entirely. This means:

1. A user who restricts Alexa to only the "Music" library will still see artists/albums from "Movies" appear in Alexa's voice recognition (SMAPI catalog + dynamic slot values)
2. When they say "Play [excluded artist]", Alexa recognizes the name but the intent handler says "not found" — confusing UX
3. There are zero handler-level integration tests verifying that excluded libraries are actually excluded from results

Additionally, `ApplyLibraryFilter` is a `protected static` method on `BaseHandler`, making it inaccessible to catalog/entity classes that live outside the handler hierarchy.

## Scope

Two subtasks:
1. **JF-139-A**: Extract shared filtering utility, thread `AllowedLibraryIds` through `DynamicEntitiesInterceptor` → `DynamicEntityBuilder` and into `LibrarySyncService.SyncCatalogAsync`
2. **JF-139-B**: Integration tests for library filtering across handlers, catalog sync, and dynamic entity building
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 LibrarySyncService.SyncCatalogAsync filters items by the user's AllowedLibraryIds (only uploads items from allowed libraries to SMAPI)
- [ ] #2 DynamicEntityBuilder.BuildSlotValues filters items by the user's AllowedLibraryIds (only populates slot values from allowed libraries)
- [ ] #3 DynamicEntitiesInterceptor passes AllowedLibraryIds (or full Entities.User) to DynamicEntityBuilder
- [ ] #4 ApplyLibraryFilter logic is extracted into a shared utility accessible to non-handler classes
- [ ] #5 Integration tests verify excluded libraries do not appear in handler search/playback results
- [ ] #6 Integration tests verify catalog sync excludes items from restricted libraries
- [ ] #7 Integration tests verify dynamic entity building excludes items from restricted libraries
- [ ] #8 All existing tests continue to pass
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
# Implementation Plan: JF-139

## Overview

Two-phase fix: (A) thread `AllowedLibraryIds` through catalog sync and dynamic entities, (B) write integration tests. Phase A must land before B.

---

## Phase A: Code Fix (JF-139-A)

### Step 1: Extract shared library filter utility

**Why:** `ApplyLibraryFilter` and `GetAllowedLibraryIds` are `protected static` on `BaseHandler`, so `LibrarySyncService` and `DynamicEntityBuilder` can't call them.

**Changes:**
- Create `Jellyfin.Plugin.AlexaSkill/Alexa/Util/LibraryFilter.cs` with a public static class `LibraryFilter` containing:
  - `Guid[]? GetAllowedLibraryIds(Entities.User? user)` — copy logic from `BaseHandler.GetAllowedLibraryIds` (lines 558-574): parse `user.AllowedLibraryIds` from strings to Guids, skip invalid, return null when null/empty
  - `void ApplyLibraryFilter(InternalItemsQuery query, Entities.User? user)` — copy logic from `BaseHandler.ApplyLibraryFilter` (lines 581-588): call `GetAllowedLibraryIds`, set `query.TopParentIds` if non-null
- In `BaseHandler.cs`, change the two existing methods to delegate to `LibraryFilter` (one-liner wrappers). This keeps backward compat for all 30+ handler call sites.
- All existing `LibraryFilterTests` in `FeatureFlagTests.cs` should continue to pass since the logic is identical.

**Files:**
- NEW: `Alexa/Util/LibraryFilter.cs`
- EDIT: `Alexa/Handler/BaseHandler.cs` (lines 558-588, delegate to utility)

### Step 2: Thread AllowedLibraryIds into LibrarySyncService

**Why:** `SyncCatalogAsync` uploads ALL items regardless of user restrictions.

**Changes in `Alexa/Catalog/LibrarySyncService.cs`:**
- The method already receives `Entities.User user` as a parameter (alongside `JellyfinUser`).
- After constructing the `InternalItemsQuery` (line ~155), call `LibraryFilter.ApplyLibraryFilter(query, user)` before `GetItemList`.
- This applies `TopParentIds` so only items from allowed libraries are fetched.

**Important consideration:** Catalog sync may be invoked per-user or globally. Check the caller — if it's called once with a specific user, the fix is straightforward. If it's called globally (no specific user), we need to decide whether to filter at all. Check `CatalogManager` or whichever class calls `SyncCatalogAsync` to understand the invocation pattern.

**Files:**
- EDIT: `Alexa/Catalog/LibrarySyncService.cs` (~line 155-167)

### Step 3: Thread AllowedLibraryIds through DynamicEntitiesInterceptor → DynamicEntityBuilder

**Why:** `DynamicEntityBuilder` only receives a `Guid jellyfinUserId` and can't access `AllowedLibraryIds`.

**Changes in `Alexa/DynamicEntities/DynamicEntitiesInterceptor.cs`:**
- The interceptor already resolves the `Entities.User` via `_config.GetUserByPersonId()` or access token fallback (lines ~60-80).
- Instead of only passing the `jellyfinUserId` Guid to `_dynamicEntityBuilder.UpdateDynamicEntitiesAsync()`, pass `allowedLibraryIds` (the Guid array from `LibraryFilter.GetAllowedLibraryIds(user)`) as an additional parameter.

**Changes in `Alexa/DynamicEntities/DynamicEntityBuilder.cs`:**
- Update `BuildSlotValues` signature to accept `Guid[]? allowedLibraryIds` (nullable, null = unrestricted).
- After constructing the `InternalItemsQuery` (line ~122), apply `TopParentIds = allowedLibraryIds` when non-null.
- Update `UpdateDynamicEntitiesAsync` to pass the new parameter through.

**Files:**
- EDIT: `Alexa/DynamicEntities/DynamicEntitiesInterceptor.cs` (~lines 60-80)
- EDIT: `Alexa/DynamicEntities/DynamicEntityBuilder.cs` (~lines 115-130)

### Step 4: Verify build and existing tests

- `dotnet build` — must pass with 0 errors
- `dotnet test` — all existing tests must pass
- The existing `LibraryFilterTests` (7 tests in `FeatureFlagTests.cs`) validate the extracted logic still works

---

## Phase B: Integration Tests (JF-139-B, depends on A)

### Step 5: Handler-level library filter tests

**New file:** `Jellyfin.Plugin.AlexaSkill.Tests/Handler/LibraryFilterIntegrationTests.cs`

Tests to write:
1. **PlaySong handler with restricted libraries** — set `AllowedLibraryIds = [musicLibId]` on user, mock `ILibraryManager.GetItemList`, verify the query's `TopParentIds` equals `[musicLibId]`
2. **SearchMedia handler with restricted libraries** — same pattern, verify search queries are filtered
3. **Handler with null AllowedLibraryIds** — verify query has no `TopParentIds` set (unrestricted)
4. **Handler with empty AllowedLibraryIds** — same as null (unrestricted)

Pattern: follow existing handler test pattern (mock `ILibraryManager`, `IUserManager`, set up `Entities.User` with `AllowedLibraryIds`, invoke handler, inspect the query passed to the mock).

### Step 6: LibrarySyncService filter tests

**New file or extend:** `Jellyfin.Plugin.AlexaSkill.Tests/Catalog/LibrarySyncServiceTests.cs`

Tests to write:
1. **SyncCatalogAsync with allowed libraries** — user has `AllowedLibraryIds = [libA, libB]`, verify only items from libA and libB are fetched (check `InternalItemsQuery.TopParentIds` on the mock)
2. **SyncCatalogAsync with null AllowedLibraryIds** — unrestricted, verify no `TopParentIds` set
3. **SyncCatalogAsync with invalid GUIDs** — all invalid → treated as unrestricted

### Step 7: DynamicEntityBuilder filter tests

**New file or extend:** `Jellyfin.Plugin.AlexaSkill.Tests/DynamicEntities/DynamicEntityBuilderTests.cs`

Tests to write:
1. **BuildSlotValues with allowed libraries** — pass `allowedLibraryIds = [libA]`, verify query has `TopParentIds = [libA]`
2. **BuildSlotValues with null allowed libraries** — unrestricted, no `TopParentIds`
3. **BuildSlotValues with empty array** — treated as unrestricted

### Step 8: Final validation

- `dotnet build` — 0 errors
- `dotnet test` — all pass (old + new)
- No new compiler warnings

---

## Risk & Considerations

1. **Catalog sync scope**: If `SyncCatalogAsync` is called once for all users (not per-user), we may need a different approach — take the intersection of all users' allowed libraries, or sync per-user catalogs separately. Need to verify the caller pattern before implementing Step 2.

2. **Stale config reference**: `DynamicEntitiesInterceptor` uses a singleton-injected `_config`. Since Jellyfin mutates the config object in-place, this is likely fine, but worth confirming the interceptor sees config changes without restart.

3. **Performance**: Adding `TopParentIds` filtering to catalog/entity queries may change query plans. Should be a net positive (smaller result sets) but worth monitoring.

4. **ApplyLibraryFilter callers in BaseHandler**: The 5 query paths identified as missing `ApplyLibraryFilter` (PlayEpisode, YesIntentHandler.PlayAlbum/PlayPlaylist, SkillConnectionHandler, MediaInfoIntentHandler) are out of scope for this task — they use ParentId/AncestorIds constraints that provide implicit filtering. Filed separately if needed.
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Both subtasks completed. Per-user AllowedLibraryIds now enforced across all three layers: intent handlers, catalog sync (SMAPI), and dynamic entities (session-scoped NLU). 13 new integration tests, all 1332 tests pass.
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
