---
id: JF-949
title: >-
  Song catalog opt-out must TEAR DOWN: strip the SongTitle wiring from locale
  models, delete the SMAPI catalog, clear SongCatalogId (JF-946 review F1)
status: In Progress
assignee: []
created_date: '2026-11-23 06:06'
updated_date: '2026-11-24 22:00'
labels:
  - catalog
  - privacy
  - jF-946-followup
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/LibrarySyncService.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/CatalogManager.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review finding F1 from the JF-946 round: turning CatalogSongSyncEnabled off after a prior opt-in leaves the uploaded tracklist catalog wired into every synced locale model indefinitely. Mechanism: the flag-off branch only removes the Song leg from the current run's wiring table; InjectCatalogReferences is additive-only (unmapped types ride through the GET-modify-PUT untouched) and CatalogWiringGraft now explicitly PRESERVES Song wiring on every model rebuild; user.SongCatalogId is never cleared; CatalogManager has no delete-catalog call. For a gate whose stated purpose is privacy (private recordings in the tracklist), the opt-out must actually remove the data. Design sketch: (1) on a sync run where the flag is OFF but SongCatalogId != null, rewrite the locale models WITHOUT the SongTitle wiring: strip the valueSupplier from the type block and restore the static seed values (the seeds are readable from the embedded models the way CatalogSeedEnrichment reads the other types' seeds, or add a Song arm to GetSeedNames); (2) DELETE the SMAPI catalog (DELETE /v1/skills/api/custom/interactionModel/catalogs/{catalogId}, needs a CatalogManager method + the same 503-retry family as the upload); (3) clear user.SongCatalogId only after the deletes succeed; (4) wire a 'Remove uploaded song data' action next to the config toggle. Interim mitigation shipped with JF-946: the config UI discloses that turning the toggle off stops future uploads but does not delete already-uploaded data.
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
