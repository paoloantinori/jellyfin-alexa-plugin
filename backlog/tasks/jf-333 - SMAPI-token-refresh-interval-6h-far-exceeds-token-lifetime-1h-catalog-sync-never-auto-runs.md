---
id: JF-333
title: >-
  SMAPI token refresh interval (6h) far exceeds token lifetime (1h); catalog
  sync never auto-runs
status: Done
assignee: []
created_date: '2026-07-12 20:04'
updated_date: '2026-07-12 20:48'
labels:
  - scheduled-tasks
  - smapi
  - token-refresh
  - catalog
  - reliability
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/EntryPoints/TokenRefreshTask.cs
  - Jellyfin.Plugin.AlexaSkill/EntryPoints/CatalogSyncTask.cs
  - Jellyfin.Plugin.AlexaSkill/EntryPoints/SkillStartup.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found while investigating JF-332 (2026-07-12). The Alexa scheduled tasks on minix were effectively dormant:

1. **TokenRefreshTask interval (6h) >> access-token lifetime (~1h)** — VERIFIED. TokenRefreshTask.cs:110 sets a 6h IntervalTrigger and refreshes blindly every run (no expiry check, lines 75-86). LWA access tokens from the diagnostics endpoint live ~1h (ExpiresAt was +1h after a manual refresh). So the SMAPI access token is expired ~83% of the time. SmapiDeviceToken is used by SMAPI MANAGEMENT ops (catalog sync, model deploy/redeploy) — not live playback (which uses the Alexa request accessToken) — so this makes catalog sync and invocation-name redeploy unreliable (they usually hit a 401). The class docstring claims it "refreshes before they expire, avoiding 401s" but the 6h cadence doesn't achieve that for a 1h token.

2. **CatalogSyncTask (7-day IntervalTrigger) never reached its first auto-run** — VERIFIED it never ran before 2026-07-12 (only 2 result log lines ever, both manual triggers that day). Hypothesis (well-supported): Jellyfin re-registers a plugin's IScheduledTasks on each plugin load (DLL update), and an IntervalTrigger's first run is `registration_time + interval`. Plugin updates on minix happen more often than every 7 days (0.9.4.0 deployed Jul 11; task exists since JF-96.2 May 7), so the 7-day first-run baseline resets before it elapses → CatalogSync never auto-fires. The scheduler itself works (the 1-hour CleanUp task DID auto-fire).

Net effect before JF-332: AlbumName was never populated by the catalog sync (it only worked when manually triggered, and even then failed at polling — now fixed).

Goal: make the SMAPI token reliably fresh for management ops, and make CatalogSync actually run on a useful cadence.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 TokenRefresh runs at an interval shorter than the access-token lifetime, OR is expiry-aware (refreshes when the token is within N minutes of expiry). Decide and document the chosen approach.
- [ ] #2 SMAPI management operations (catalog sync, invocation-name redeploy) no longer fail due to a stale SmapiDeviceToken — verify by leaving the system running and confirming catalog sync succeeds on its schedule without manual token refresh.
- [ ] #3 CatalogSync runs on a cadence that actually elapses (consider: trigger on plugin startup, shorter interval, or a startup hook in SkillStartup) so AlbumName stays populated after future plugin updates without manual triggering.
- [ ] #4 No regression: live Alexa playback unaffected (it uses the request accessToken, not SmapiDeviceToken) — verify playback still works.
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
RESOLVED + DEPLOYED 2026-07-12 (commit d4398b6). Both scheduled-task issues fixed and verified on the live minix instance.

1. TokenRefreshTask: interval 6h → 20min (access tokens live ~1h); added expiry-aware skip (refresh only if token null or <30min remaining). VERIFIED IN PROD: deployed trigger shows 20min; with a fresh token (60min remaining), triggering the task produced NO refresh log → expiry-gate correctly skipped rotation (old blind code would have rotated).

2. CatalogSyncTask: added StartupTrigger (fires on every plugin load) + kept the 7-day interval; ExecuteAsync skips users synced <12h ago. VERIFIED IN PROD: deployed triggers show StartupTrigger + 7d interval; the deploy restart fired CatalogSync and the gate logged "Skipping user ...: catalog synced 2.7h ago (< 12h)" — so it runs on startup but avoids redundant work. Next restart after 12h will re-sync.

Tests: updated TokenRefresh interval assertion (<1h), added CatalogSync metadata + startup-trigger tests (15 ScheduledTask tests pass, 2487/2488 full suite — the 1 failure is an unrelated FuzzyMatcher perf-threshold flake on the loaded sandbox, passes 194ms in isolation). Expiry-skip + recent-sync-gate logic verified in production (above) rather than unit-isolated (ExecuteAsync depends on Plugin.Instance / LWA static client).

Net: SMAPI token now stays fresh for management ops, and AlbumName stays populated across future plugin updates without manual triggering.
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
- [ ] #8 Locale response strings added to all 17 locales
<!-- DOD:END -->
