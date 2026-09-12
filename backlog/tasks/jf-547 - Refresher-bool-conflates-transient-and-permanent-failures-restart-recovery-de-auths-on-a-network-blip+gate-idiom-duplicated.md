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
