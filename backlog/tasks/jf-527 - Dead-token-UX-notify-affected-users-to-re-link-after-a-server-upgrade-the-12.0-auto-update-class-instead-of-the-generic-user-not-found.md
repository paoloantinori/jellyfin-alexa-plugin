---
id: JF-527
title: >-
  Dead-token UX: notify affected users to re-link after a server upgrade (the
  12.0 auto-update class) instead of the generic user-not-found
status: To Do
assignee: []
created_date: '2026-09-09 07:40'
labels:
  - ux
  - account-linking
  - resilience
  - localization
dependencies: []
references:
  - JF-307
  - 'BaseHandler.cs:474'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo (2026-09-09 morning): the Jellyfin 12.0 auto-update invalidated per-user JellyfinTokens (the admin key died with 401 on every auth shape; the per-user tokens are expected dead too, unverified pending a fresh admin key). Every affected user's next Alexa request will hit the session-miss path and hear the GENERIC 'Utente non trovato, ricollega il tuo account' - which does not say WHY the link died or HOW to fix it. Track the failure at UX level and notify specifically.

The failure surface (mapped): every request resolves the session via ResolveSessionAsync(user.JellyfinToken, deviceId) -> GetSessionByAuthenticationToken; a dead token returns null -> BuildUserNotFoundResponse -> the UserNotFound tell. The user has no idea a server upgrade killed their link.

DESIGN (pre-decided, known surfaces only - no auth-manager API guessing): discriminate the dead-token shape with evidence the token PREVIOUSLY worked (DeviceQueueManager activity for the device, or the user's LastPlayedItemId set) and speak the new specific message with a card pointing to the skill settings page's link flow. The upgrade attribution stays HONEST: 'this can happen after a server update' (we cannot prove the cause from inside the request).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 New localized AccountRelinkRequired message in ALL 17 locales (plain + card variant per house conventions): explains the link stopped working (likely after a server update) and says to open the skill settings page and link again - NO code-symbol/internal language, user-facing only
- [ ] #2 Discriminator implemented at the session-miss site (BaseHandler ~:474): session==null AND user.JellyfinToken non-empty AND evidence the token previously worked for this device (DeviceQueueManager activity OR the user's LastPlayedItemId set) => the new message + card; empty-token/never-linked users keep the existing UserNotFound tell; event requests keep the keep-alive shape
- [ ] #3 Log line at the discriminator with the corr context (token prefix hash, device id, last-played evidence) for triage
- [ ] #4 Unit tests: dead-token-with-history shape => new message + card; fresh-user shape => UserNotFound unchanged; event-request shape unchanged
- [ ] #5 Full suite green; /simplify + code-review high gates before merge
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
