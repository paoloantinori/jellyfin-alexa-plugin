---
id: JF-404
title: >-
  Catalog injection invisible in committed models - document runtime
  valueCatalog architecture for reproducibility
status: Done
assignee: []
created_date: '2026-08-23 05:57'
updated_date: '2026-08-23 06:27'
labels:
  - documentation
  - interaction-model
  - catalog
milestone: m-17
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Reproducibility finding (2026-08-23 audit). The committed model_*.json files contain no valueSupplier/valueCatalog references: catalog sync (JellyfinArtist, AlbumName phonetic synonyms) happens out-of-band at runtime via SMAPI (CatalogSyncService). Consequence: the interaction-model state a real user has on Amazon cannot be reconstructed from the repo, and a reviewer/contributor cannot tell from the JSONs that catalog injection exists. Low effort, high clarity win: document the runtime catalog-injection architecture in README (or docs/) with a diagram of what the committed models contain vs what gets injected at runtime, and add a short pointer comment at the top of each model JSON generator/template explaining the same. Alternatively/additionally: add a validate-script check that asserts no locale defines a catalog-backed slot statically (guarding accidental divergence).
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Done: README 'Testing' section now has 'What the committed models contain (and what they don't)' explaining static base vs runtime CatalogSyncService injection, phonetic synonyms, non-reproducibility from repo alone, and the triage rule (check catalog sync before JSONs). Skipped the validate-script guard (low value: nothing statically defines catalog slots today) and the per-JSON generator comments (README + CLAUDE.md cover it).
<!-- SECTION:FINAL_SUMMARY:END -->
