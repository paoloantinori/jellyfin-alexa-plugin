---
id: JF-552
title: >-
  Embedded-model rebuild PUTs wipe catalog wiring
  (SeriesName/JellyfinArtist/AlbumName fall back to the 8-value static seed
  until the next 12h catalog sync)
status: To Do
assignee: []
created_date: '2026-09-12 22:38'
updated_date: '2026-09-13 09:33'
labels:
  - bug
  - catalog
  - interaction-model
  - reliability
dependencies: []
references:
  - JF-549
  - JF-493
  - JF-543
  - Jellyfin.Plugin.AlexaSkill/Plugin.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/ModelDeployment/InteractionModelRedeployer.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/LibrarySyncService.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found live 2026-09-13 00:33 (JF-549 post-deploy verification). Plugin.BuildSkillInteractionModels (Plugin.cs:223) builds models from the embedded resources + mood overrides ONLY; it never re-injects the catalog wiring (valueSupplier/valueCatalog). So every embedded-model PUT - the custom-model/rebuild endpoint, the invocation-name save path, and the LWA-controller redeploy - replaces the LIVE catalog-wired slot types with the 8-value static seed, silently downgrading one-shot resolution for every non-seed series/artist/album until the next 12h catalog sync re-wires them.

Evidence: 2026-09-12 17:40 profile-nlu (pre-rebuild, catalog-wired live model): 'riproduci Adolescence stagione uno episodio due' filled series_name=Adolescence ER_SUCCESS_MATCH (catalog id jellyfin_series_422853a1...). After the 2026-09-13 00:29 JF-549 it-IT rebuild (which PUT the embedded model), the SAME probe returns series_name EMPTY twice (stable), while seed series (breaking bad, stranger things) still fill from the static values; get-interaction-model confirms the live SeriesName type now has only static values, no valueSupplier. The wiring is restored only when the catalog sync runs (LibrarySyncService GETs the live model, swaps the type definitions, PUTs back - so the sync itself preserves other live model content).

Fix shape: factor the type-injection logic the catalog sync uses so BuildSkillInteractionModels can apply it from the user's persisted catalog ids (SeriesCatalogId, ArtistCatalogId, AlbumCatalogId) + the JF-543 CatalogWiringUnsupportedLocales guard + the JF-495 stale-pin guard (no id without a fresh version). The redeployer then needs the user passed through (it already has User for the SMAPI calls).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Live verification: after a rebuild, profile-nlu resolves a non-seed catalog series (e.g. Adolescence) into series_name immediately, without waiting for the next catalog sync
- [ ] #2 A rebuild/redeploy (custom-model/rebuild endpoint, invocation-name save, SkillStartup push, LWA linking, CreateSkillAsync - all funnel through SmapiManagement.UpdateInteractionModelsAsync) PUTs models whose catalog-backed slot types carry the valueSupplier PRESERVED FROM THE LIVE MODEL's wiring (GET live -> graft), not the static seed
- [ ] #3 Unit tests: the graft path with a wired live model produces valueSupplier.valueCatalog.catalogId set and no static seed alongside (CatalogWiringGraftTests + SmapiManagementWiringTests through the client seam); JF-543 locales skip the GET entirely
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
DESIGN (locked 2026-09-13 after probes; /simplify-amended):

FACTS (probed on the shipped artifacts):
- Alexa.NET.Management 5.10.0 typed SkillInteraction CANNOT carry valueSupplier/valueCatalog: reflection shows SlotType.ValueSupplier has only a Type property, and a Newtonsoft round-trip of a wired envelope DROPS the catalog block (why the sync uses raw PUTs).
- Our SkillInteractionModel : SkillInteractionContainer serializes directly to the SMAPI envelope {"interactionModel":{"languageModel":...}} including types (probed).
- ALL five embedded-model writer flows (custom-model/rebuild, invocation-name save, SkillStartup version push, LWA account-linking, CreateSkillAsync) funnel through SmapiManagement.UpdateSkillAsync -> UpdateInteractionModelsAsync (single choke point).

IMPLEMENTATION (as landed):
1. CatalogManager.InjectCatalogReferences (+ helpers) static with ILogger param; new LocaleModelUrl single owner; CreateAuthorizedGet internal.
2. CatalogWiring record + CatalogWiringGraft.ExtractWiring/Apply (pure JSON; JF-543 guard; delegates to the ONE injection implementation).
3. SmapiManagement.PutLocaleModelPreservingWiringAsync: serialize -> JF-543 pre-guard -> GET live (GetLiveModelJsonAsync, null-tolerant) -> graft -> raw PUT via Plugin.HttpClient with a per-call Func test seam (RawModelClientOverrideForTests, LwaClient pattern). Audit line + RetryHelper + failedLocales bookkeeping unchanged.
4. Custom-URL/restore path (ModelDeploymentManager) stays typed/unwired: documented residual, tracked in JF-554 together with the raw-PUT transport consolidation (settle-wait for the graft GET, shared service, handler lifetimes, test handler fakes).
5. Deploy + live AC#3: rebuild it-IT -> immediately profile-nlu 'riproduci Adolescence stagione uno episodio due' must still fill series_name (no catalog sync in between).
<!-- SECTION:PLAN:END -->

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
