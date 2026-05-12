---
id: JF-125
title: User-aware favorites by name ("Play Paolo's favourites")
status: To Do
assignee: []
created_date: '2026-05-12 04:45'
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

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
