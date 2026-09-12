---
id: JF-545
title: >-
  Consolidate the three LWA token-refresh implementations onto
  SmapiTokenRefresher and gate sibling long SMAPI ops (unfiled JF-544 /simplify
  promise)
status: Done
assignee: []
created_date: '2026-09-12 07:53'
updated_date: '2026-09-12 10:10'
labels: []
dependencies: []
references:
  - commit a7a55961
  - >-
    backlog/tasks/jf-544 -
    Catalog-sync-outlives-the-LWA-access-token-401-mid-sync-at-leg-6-16-locale-run-aborted-refresh-before-long-op-per-leg-re-read-401-retry.md
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Commit a7a55961 (JF-544) introduced SmapiTokenRefresher (Jellyfin.Plugin.AlexaSkill/Lwa/SmapiTokenRefresher.cs) whose class doc claims to be "The one LWA device-token refresh implementation", and its commit message claims the migration of the two remaining inline copies was "filed from the /simplify review as future work in the task notes". Verified 2026-09-12: the JF-544 task file contains no such note and no backlog task mentions RefreshAndRetry or SmapiTokenRefresher; this task makes that promise real.

The three implementations and their divergences:
- SmapiTokenRefresher.RefreshAsync: never throws (returns bool), checks LWA credential presence, refreshes from user.SmapiRefreshToken, persists per refresh via SaveConfiguration.
- AlexaUtil.RefreshAndRetry (AlexaUtil.cs ~lines 39-56): no try/catch around the LwaClient.RefreshDeviceToken HTTP call, no credential-presence check, throws UnauthorizedAccessException when the refresh returns null, rethrows the original 401 when SmapiDeviceToken is null. Used by request-path 401 recovery via AlexaUtil.CallAsync (SmapiManagement callers).
- SkillStartup.cs ~lines 135-158 (restart recovery): its own DeviceToken construction + assign + persist, and on failure it nulls user.SmapiRefreshToken and sets UserSkillStatus.LwaAuthPending (a de-authorization policy RefreshAsync deliberately does not own). When migrating, preserve that failure-branch policy explicitly (parameter or callback) rather than deleting it.

Also verified uncovered by any 401/refresh defense (same JF-544 incident class survives there): ModelDeploymentManager (custom-model deploy/restore, reached from ConfigurationController ~lines 786/851) pins user.SmapiManagement (and its access token) at construction and turns any mid-op 401 into a failed result; the 17-locale redeploy batch (SmapiManagement.UpdateInteractionModelsAsync) swallows per-locale 401s into failedLocales without refreshing mid-batch (its polling/canary legs do recover via AlexaUtil.CallAsync).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 AlexaUtil.RefreshAndRetry delegates its refresh to SmapiTokenRefresher.RefreshAsync (or is deleted in favor of it), preserving or explicitly relocating its divergent behaviors; no second LwaClient.RefreshDeviceToken call site remains outside SmapiTokenRefresher except SkillStartup's policy-bearing copy
- [ ] #2 SkillStartup restart recovery delegates the mechanical refresh/persist to SmapiTokenRefresher while keeping the null-SmapiRefreshToken + LwaAuthPending de-authorization failure policy, or documents in SmapiTokenRefresher why the split is deliberate
- [ ] #3 SmapiTokenRefresher's class doc no longer overclaims ('one implementation') once the above land, or the claim is true
- [ ] #4 ModelDeploymentManager custom-model deploy/restore path refreshes the token before the operation and/or recovers a mid-op 401 via SmapiTokenRefresher instead of failing the deployment
- [ ] #5 A unit test pins the never-throws contract of RefreshAsync on the LWA HTTP failure path (e.g. handler throwing / returning error) so the contract cannot silently regress
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
RESOLVED 2026-09-12 (commits 2bcbf96e + 8061a3a2): all five ACs met. AC1: AlexaUtil delegates, no second RefreshDeviceToken producer site (LWAController's initial issuance and ProactiveEventClient's client_credentials grant are different lifecycles, correctly outside). AC2: SkillStartup delegates with its policy preserved AND the reviewer proved the null-return tightening was moot (ParseTokenResponse never returns null; dead branch + nullable signature removed). AC3: the doc now says mechanism + policy wrappers. AC4: deploy+restore gated and 401-recovering. AC5: never-throws pinned on 3 HTTP failure modes. Gates: /simplify 4-angle (real logger, dead-null cleanup, comment fixes) + code-review high (10 findings: 4 applied in the review-fix commit, 4 filed as JF-547 items 3-6, 2 pre-existing low/dead-code notes); suite 3663/3663. NOT deployed live (repo-only consolidation; the deployed JF-544 build already carries the sync-side defenses; this rides the next deploy).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Consolidated the three LWA token-refresh implementations onto SmapiTokenRefresher: AlexaUtil.RefreshAndRetry (request path) and SkillStartup's restart recovery now delegate the mechanical refresh/persist to the one mechanism, each keeping its own policy (throw-on-failure for the request path with a real logger so the invalid_grant body reaches the logs; de-authorization for restart recovery, which the review verified is behavior-preserved since the old code already de-authed on every reachable failure and the null return was dead code, now removed along with the dishonest Task<DeviceToken?> signature). ModelDeploymentManager's custom-model deploy AND restore path (restore delegates to deploy) gained a pre-deploy refresh gate (10-min budget, 5x the worst-case operation, moved after the pure-local validations so rejected requests do not rotate the token for nothing) and a one-shot 401 PUT retry with client recreation carrying the concurrent-rotation guard. Five new tests pin the never-throws contract on three HTTP failure modes via the new LwaClient test seam (the JF-366 pattern), the rotate-and-persist happy path, and the no-credentials short-circuit (now at Warning, since restart recovery de-auths on it). Review residuals filed as JF-547 items 3-6 (the third 401-copy consolidation, the cancellation-swallow class, the request-path persist contract decision, the log-level reroute).
<!-- SECTION:FINAL_SUMMARY:END -->

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
