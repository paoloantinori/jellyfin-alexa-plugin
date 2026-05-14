---
id: JF-125
title: User-aware favorites by name ("Play Paolo's favourites")
status: Done
assignee: []
created_date: '2026-05-12 04:45'
updated_date: '2026-05-12 12:12'
labels:
  - enhancement
  - favorites
  - multi-user
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Allow users to play another user's favorites by name, e.g., "Play Paolo's favourites". Currently PlayFavoritesIntent only plays the authenticated user's favorites. Cross-user access is useful in multi-user households.

Inspired by JellyMusic's user-aware favorites: resolves Jellyfin users by name via `/Users` endpoint with fuzzy matching (token_sort_ratio > 60 threshold).

Implementation: Add an optional `username` slot to PlayFavoritesIntent. If provided, query `/Users` to resolve the name, then fetch their favorites. If no slot, use the authenticated user as now.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 'Play {username}'s favourites' resolves the Jellyfin user by name
- [ ] #2 Fuzzy matching applied to username resolution
- [ ] #3 Falls back to authenticated user if no username slot provided
- [ ] #4 Localized in all 12 locales
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
PlayFavoritesIntentHandler accepts optional username slot. Resolves Jellyfin users by name with fuzzy matching. Added username slot to all 12 interaction models and UserByNameNotFound string to all 12 locales. 15 unit tests. Build clean, 1087 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
- [ ] #2 dotnet build passes with 0 errors
- [ ] #3 dotnet test passes
- [ ] #4 No new compiler warnings introduced
- [ ] #5 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #6 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #7 NLU test fixtures updated if interaction model changed
- [ ] #8 E2E test added for new intent or handler logic
- [ ] #9 Locale response strings added to all 12 locales
<!-- DOD:END -->
