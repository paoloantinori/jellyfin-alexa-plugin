---
id: JF-596
title: >-
  Model rebuild strips catalog wiring (ER dead window until next catalog sync):
  re-inject references on the rebuild path
status: To Do
assignee: []
created_date: '2026-09-19 23:16'
labels:
  - bug
  - catalog
  - deploy
  - reliability
milestone: Polish
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
OPERATIONAL BUG found via the 2026-09-19 full E2E+NLU run (66 NLU failures, root-caused): the standard deploy step "Rebuild models" PUTs the EMBEDDED interaction models, which carry NO catalog references - stripping the valueSupplier.valueCatalog wiring that the catalog sync had injected into the LIVE models. The CatalogSyncTask then re-injects only on its next run, and its 12h skip gate (JF-333) can leave the ER dead for hours. Tonight's timeline: morning deploy+rebuild (wiring stripped) -> 10:28 startup sync SKIPPED (12h gate) -> ER dead all day -> E2E two-step and 66 NLU failures in catalog-dependent routings (bare video titles falling to PlayNextIntent free-text, search verbs NO_SELECT, album slot not filling). The CLAUDE.md troubleshooting entry "The skill behaves inconsistently (works for some names, not others) after a deploy" is likely THIS bug, not propagation lag. Recovery executed 2026-09-20 01:10: manual catalog sync (1133 artists, 886 albums, 138 series; 3 catalogs x locales wired; all model builds SUCCEEDED).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Reproduce mechanically: rebuild models, then show the live model lost catalog references (valueSupplier.valueCatalog) until a catalog sync re-injects them
- [ ] #2 Fix: the rebuild path (custom-model/rebuild locale=*) either re-injects the current catalog references after the PUT (wiring-only pass, no full catalog upload), or the CatalogSyncTask 12h gate is bypassed when the model PUT just stripped wiring (detectable: catalog versions exist but the model carries no valueSupplier)
- [ ] #3 Verified live: after a plain rebuild, profile-nlu still resolves catalog-backed entities (no ER dead window)
- [ ] #4 Suite green both TFMs; no new warnings
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
