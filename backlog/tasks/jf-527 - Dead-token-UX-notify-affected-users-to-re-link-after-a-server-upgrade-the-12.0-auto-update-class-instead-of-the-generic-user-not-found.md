---
id: JF-527
title: >-
  Dead-token UX: notify affected users to re-link after a server upgrade (the
  12.0 auto-update class) instead of the generic user-not-found
status: Done
assignee: []
created_date: '2026-09-09 07:40'
updated_date: '2026-09-09 09:27'
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
- [x] #1 New localized AccountRelinkRequired message in ALL 17 locales (plain + card variant per house conventions): explains the link stopped working (likely after a server update) and says to open the skill settings page and link again - NO code-symbol/internal language, user-facing only
- [x] #2 Discriminator implemented at the session-miss site (BaseHandler ~:474): session==null AND user.JellyfinToken non-empty AND evidence the token previously worked for this device (DeviceQueueManager activity OR the user's LastPlayedItemId set) => the new message + card; empty-token/never-linked users keep the existing UserNotFound tell; event requests keep the keep-alive shape
- [x] #3 Log line at the discriminator with the corr context (token prefix hash, device id, last-played evidence) for triage
- [x] #4 Unit tests: dead-token-with-history shape => new message + card; fresh-user shape => UserNotFound unchanged; event-request shape unchanged
- [x] #5 Full suite green; /simplify + code-review high gates before merge
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped, merged (c6cbf5e6 + merge), deployed with the JF-307p2 batch. The session-miss path now speaks a specific re-link message with a StandardCard when the discriminator fires (non-empty JellyfinToken + per-device last-played evidence via DeviceQueueManager's in-memory ConcurrentDictionary - zero per-request I/O): 'the link to your Jellyfin server stopped working, this can happen after a server update, open the skill settings page in the Jellyfin dashboard and link your account again'. Fresh users keep UserNotFound; event requests keep the JF-507 keep-alive (checked FIRST, before the discriminator); the log line carries a 12-hex SHA-256 token fingerprint for triage. 17 locales, both keys, validate_locales PASS. Review verdict SAFE TO MERGE with ZERO findings at/above threshold (discriminator per-device correctness, Tell+card shape, JF-387 mechanical check, all 17 locale texts read clean, tests pin card CONTENT); 4 below-threshold notes filed as JF-528 same-turn. Tests 3530/3530. This is the UX half of the 12.0 auto-update response; the operational half (fresh admin key + token re-link verification) stays in JF-307's notes.
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
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
