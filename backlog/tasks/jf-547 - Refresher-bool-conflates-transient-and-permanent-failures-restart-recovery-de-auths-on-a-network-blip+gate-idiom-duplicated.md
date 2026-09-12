---
id: JF-547
title: >-
  Refresher bool conflates transient and permanent LWA failures (restart recovery
  de-auths on a network blip); pre-op refresh-gate idiom duplicated
status: To Do
created_date: '2026-09-12'
labels: [reliability, token-refresh]
references:
  - Jellyfin.Plugin.AlexaSkill/Lwa/SmapiTokenRefresher.cs
  - Jellyfin.Plugin.AlexaSkill/EntryPoints/SkillStartup.cs
dependencies: [JF-545]
---

## Description

Filed from the JF-545 /simplify pass (2026-09-12), two residuals the consolidation deliberately did not absorb:

1. TRANSIENT-VS-PERMANENT: RefreshAsync returns a single bool, so SkillStartup's restart recovery treats a transient failure (network down, LWA 5xx) exactly like a permanent one (invalid_grant, revoked): it nulls SmapiRefreshToken and sets LwaAuthPending, forcing a manual re-link after a mere blip at boot. Fix shape: distinguish failure classes in the refresher's result (enum or out-param: Transient | Permanent | NotConfigured), and have restart recovery de-auth only on Permanent (plus a bounded retry for Transient). The JF-545 comment in SkillStartup marks the spot.

2. GATE IDIOM DUPLICATED: the pre-operation refresh gate (RemainingLifetime < budget -> log -> RefreshAsync -> warn-and-continue) now exists twice nearly verbatim (LibrarySyncService pre-sync, ModelDeploymentManager pre-deploy) with TokenRefreshTask as an inverse-shape third. Fix shape: SmapiTokenRefresher.EnsureLifetimeBudgetAsync(user, budget, logger, opName) collapsing them.

## Definition of Done
- [ ] dotnet build passes with 0 errors
- [ ] dotnet test passes
- [ ] No new compiler warnings introduced
- [ ] Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] HttpClient instances are not shared across calls that modify BaseAddress
- [ ] NLU test fixtures updated if interaction model changed
- [ ] E2E test added for new intent or handler logic
- [ ] Locale response strings added to all 17 locales
- [ ] /simplify passed (no blocking cleanups remaining)
- [ ] /code-review high passed (no blocking findings remaining or findings applied/tracked)

RESIDUALS JOINED from the JF-545 code-review (2026-09-12, all verified; the mechanical ones - the retry's concurrent-rotation guard, the gate-after-pre-flight move, the credentials-miss log level, the test state restore - were applied in the review-fix commit):

3. THREE 401-RECOVERY COPIES: the SMAPI 401-refresh-retry policy now exists in three divergent shapes (AlexaUtil.CallAsync's Refit two-arm, LibrarySyncService's HttpRequestException leg-retry with the rotation guard, ModelDeploymentManager's Refit PUT-retry that now ALSO has the guard). They already disagree on failed-refresh semantics. Fix shape: one shared recovery helper (or route the deploy PUT through CallAsync with a closure re-reading user.SmapiManagement); the transient/permanent split in item 1 must land in ONE place, not three.

4. DeployCustomModelAsync's outer catch (Exception) swallows OperationCanceledException, so the controller's TimeoutException-to-504 arm is unreachable for the deploy/restore phases (a 5-min CTS cancellation reads as a generic failed-deploy 500; the fetch phase's arm is dead too - cancellation there surfaces as TaskCanceledException). Fix shape: rethrow OperationCanceledException/TaskCanceledException from the catch (or catch them first).

5. REQUEST-PATH PERSIST CONTRACT: CallAsync's refresh now persists best-effort (SmapiTokenRefresher swallows save failures) where the old inline code failed the request on a save failure. The displaced-failure window (on-disk refresh token stale after a restart -> forced re-link) is commented in the refresher but is a real caller-visible change on the request path. Decide: accept (document) or fail-loud on the request path specifically.

6. LOG-LEVEL REROUTE: transport-class refresh failures on the startup path now surface as UnauthorizedAccessException -> SkillStartup's Warning arm ('skill sync deferred', message-only) instead of the old Error arm with the stack. Operators grepping Error for startup SMAPI outages no longer see refresh failures; the evidence lives in the LwaClient-category Warning. Accept or re-route.
