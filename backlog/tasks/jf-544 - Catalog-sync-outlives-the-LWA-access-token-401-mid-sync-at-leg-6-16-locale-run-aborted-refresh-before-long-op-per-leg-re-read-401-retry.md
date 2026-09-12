---
id: JF-544
title: >-
  Catalog sync outlives the LWA access token: 401 mid-sync at leg 6 (16-locale
  run aborted); refresh-before-long-op + per-leg re-read + 401 retry
status: Done
assignee: []
created_date: '2026-09-12 07:03'
updated_date: '2026-09-12 08:50'
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
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
RESOLVED + DEPLOYED + LIVE-VERIFIED 2026-09-12 (commits a7a55961 + 8674ad84, md5 70c9fd2a, deployed 10:05). FIX (3 layers + hardening): SmapiTokenRefresher is the one shared refresh implementation with a true never-throws contract (HTTP failure caught; SaveConfiguration best-effort with the rotation kept in memory - the token IS fresh, a failed save neither aborts the sync nor fails the refresh, re-link risk logged loudly); the sync refreshes before starting when remaining < 45 min, re-reads the CURRENT token per attempt inside the leg, and retries a leg once on HttpRequestException-401 (proceeding to attempt 2 also when our refresh failed but the 20-min sweep rotated the token meanwhile); locale resolution takes the current token and a 401 there now fails loudly instead of silently degrading to it-IT (which stamped success and gated 16 locales out for 12h); the LWA refresh call got a 15s retry budget (was unbounded: a wedged endpoint could stall ~6.7 min per retry); TokenRefreshTask delegates to the helper and Debug-logs its skip with minutes-remaining.

TESTS: expired token + no LWA creds proceeds with the leg; mid-sync 401 + no refresh available fails the leg after exactly one attempt AND asserts the retry warning fired (a typed-exception refactor killing the retry filter now fails the suite). The happy retry path (refresh succeeds, leg passes) is untestable with the static LwaClient - live-verified instead.

LIVE VERIFICATION (10:12-10:38, forced via the minix window): 16/16 locales completed in 26 min 11 s (82-123 s per leg), ZERO 401s, zero retries needed, and the final skill state is 17/17 SUCCEEDED with ar-SA untouched. This is the same operation that aborted at leg 6 this morning.

Gates: /simplify 4-angle (helper catch, stale comment, loop restructure; one reviewer finding rejected with reason - the !legSucceeded flag was load-bearing, not dead) and code-review high (6 CONFIRMED findings: F1/F2/F3/F4/F7 applied across the two commits; F6 full-leg-replay accepted as bounded cost, documented; F5/F8/F10 filed as JF-545 + JF-546 by the reviewer). Suite 3658/3658 twice.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
The 16-locale catalog sync outlived its ~1h LWA access token (26-45 min run) and aborted at leg 6 with an unrecoverable 401. Fixed with three layers plus review hardening: a shared SmapiTokenRefresher with a true never-throws contract (best-effort config save keeps the fresh token in memory), a pre-sync refresh when remaining < 45 min, per-attempt current-token reads inside the leg with a one-shot 401 retry (proceeding to attempt 2 when the 20-min sweep rotated the token even if our own refresh failed), a loud failure instead of silent it-IT degradation when locale resolution itself 401s, a 15s retry-chain budget on the LWA refresh call, and Debug-logged skip decisions in the periodic task. A final /simplify pass removed the dead catch and six other minors; its logger-capture consolidation went to JF-546, and the sibling-path/refresh-copy consolidations to JF-545. Live-verified end to end: the identical operation that aborted at leg 6 completed 16/16 locales in 26 min 11 s with zero 401s and a 17/17-SUCCEEDED skill.
<!-- SECTION:FINAL_SUMMARY:END -->
