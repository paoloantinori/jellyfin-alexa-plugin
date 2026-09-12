---
id: JF-544
title: >-
  Catalog sync outlives the LWA access token: 401 mid-sync at leg 6 (16-locale
  run aborted); refresh-before-long-op + per-leg re-read + 401 retry
status: In Progress
assignee: []
created_date: '2026-09-12 07:03'
updated_date: '2026-09-12 07:03'
labels:
  - reliability
  - smapi
  - token-refresh
  - catalog
dependencies:
  - JF-333
references:
  - JF-333 (the original fix this extends)
  - JF-543 (the verification run that exposed it)
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/LibrarySyncService.cs
  - Jellyfin.Plugin.AlexaSkill/EntryPoints/TokenRefreshTask.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
LIVE INCIDENT 2026-09-12 08:14-08:25 CEST (during the JF-543 fix-verification forced sync): the 16-locale catalog sync aborted at leg 6 with 'SMAPI request failed: 401 Unauthorized. Body: {"message":"Token is invalid/expired."}' on the artist-catalog upload; legs it-IT..en-GB (5) had succeeded. TokenRefreshTask (JF-333's fix: 20-min interval, expiry-aware skip at <30 min remaining) IS deployed and fired a successful refresh 10 minutes after the death (08:35 CEST), so the task itself works.

ROOT CAUSE CLASS: LibrarySyncService.SyncUserLibraryAsync snapshots user.SmapiDeviceToken.AccessToken ONCE at sync start and threads that string through every leg (catalog uploads + model GET/PUT, ~100s per leg, 30-45 min for 16-17 locales). A 1h LWA access token can expire (or be rotated server-side) mid-sync; the 30-min skip margin in the refresh task does not protect an operation that outlives the token, and the sync has no recovery. Contributing opacity: the refresh task's skip path logs NOTHING (silent continue), which made the incident timeline unreconstructible from logs (on-disk ExpireTimestamp after the 08:35 refresh was +33 min, consistent with ~1h lifetime; task last-execution timestamps don't cleanly explain why the gate let the sync start with a token that died 10 min in - possibly the persisted ExpireTimestamp overstated remaining life at that firing).

FIX DIRECTION:
1. Refresh-before-long-op: at sync start, if the token has less remaining than the expected sync duration (~45 min for 17 locales), refresh inline BEFORE snapshotting; share ONE refresh implementation with TokenRefreshTask (extract a helper, do not copy).
2. Per-leg token re-read: each leg re-reads user.SmapiDeviceToken.AccessToken at leg start instead of using the start-of-sync snapshot, so a background rotation mid-sync is picked up.
3. 401-retry: on a per-leg 401 from SMAPI, refresh once and retry the leg (idempotent: catalog version creation and model PUT are both safe to re-submit).
4. Forensics: the refresh task's skip path logs at Debug with minutes-remaining, so the next incident is reconstructible.
5. Verify live: after deploy, force a sync via the minix window and confirm all 16 legs complete.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
