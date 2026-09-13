---
id: JF-554
title: >-
  Consolidate the two SMAPI raw model-PUT legs behind one service (settle-wait
  for the graft GET, ModelDeploymentManager typed-PUT residual, test handler
  fakes unification)
status: To Do
assignee: []
created_date: '2026-09-13 09:34'
labels:
  - refactor
  - reliability
  - catalog
  - test-infra
dependencies: []
references:
  - JF-552
  - JF-495
  - JF-497
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/CatalogManager.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/SmapiManagement.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/ModelDeployment/ModelDeploymentManager.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-552 /simplify altitude review (2026-09-13, same-turn rule). The JF-552 fix single-sourced the SEMANTIC core (InjectCatalogReferences) but deliberately left the raw model-PUT TRANSPORT duplicated (~25 lines: URL, bearer, send, success-check) between the catalog sync leg (CatalogManager.UpdateInteractionModelAsync) and the new SmapiManagement.PutLocaleModelPreservingWiringAsync. Consolidating inside JF-552 would have put the 12h production sync path inside the blast radius of a bug fix.

Folded residuals, all natural parts of the same service:
1. The graft GET lacks the JF-495 WaitForLocaleBuildToSettleAsync pre-wait the sync leg has: an in-flight catalog-sync build can make the graft read the last-SUCCEEDED (stale) wiring version. Accepted for JF-552 (immutable catalog content per version = pins yesterday's values at worst); the service removes the accepted race.
2. ModelDeploymentManager.DeployCustomModelAsync (:323) still PUTs typed/unwired: a custom-model RESTORE (from embedded) still wipes wiring; the custom-URL deploy arguably intends verbatim content (document the call).
3. Test handler fakes: five SMAPI-shaped private fakes across the suites (see AC#4); the repo convention is the hoisted ONE fake (JF-440/JF-465/JF-520 pattern).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One service owns the SMAPI raw model-PUT transport (LocaleModelUrl, bearer auth, GET, JF-495 settle-wait before any live-model GET, audit line, PUT, JF-497 poll dispatch); both the catalog sync leg and SmapiManagement's wiring-preserving PUT consume it
- [ ] #2 ModelDeploymentManager's custom-URL/restore path (typed InteractionModel.Update, still drops catalog wiring on restore-from-embedded) either consumes the service with the graft or documents why custom-URL deploys stay unwired
- [ ] #3 The JF-552 graft GET gains the settle-wait so an in-flight catalog-sync build cannot make the graft read a stale wiring version
- [ ] #4 The test-side SMAPI handler fakes (CatalogManagerPollingTests.FakeSmapiHandler, LibrarySyncServiceSeriesTests.FakeSmapiHandler, ModelPutFakeHandler, CatalogUploadFakeHandler, SmapiManagementWiringTests.RecordingHandler) collapse to ONE shared recorder/responder in TestHelpers or a single fake file
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
