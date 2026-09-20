---
id: JF-596
title: >-
  Model rebuild strips catalog wiring (ER dead window until next catalog sync):
  re-inject references on the rebuild path
status: Done
assignee: []
created_date: '2026-09-19 23:16'
updated_date: '2026-09-20 01:47'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-20 02:10 CORRECTION before implementation: the graft mechanism ALREADY exists on the rebuild path - UpdateSkillAsync -> UpdateInteractionModelsAsync -> PutLocaleModelPreservingWiringAsync (SmapiManagement.cs:200-228) GETs the live model, ExtractWiring, Apply, with the JF-552/555 no-swallow retry hardening. So 'rebuild strips wiring' is NOT proven; tonight's 00:20 identical-content rebuild should have preserved whatever was live. The AC#1 mechanical repro is now THE decisive experiment: rebuild, then live-GET and diff valueSupplier before/after. ALTERNATIVE deeper hypothesis: the Sep-16 skill recreation orphaned the catalog associations server-side (the first deploy of the recreated skill is necessarily unwired; a catalog SYNC re-creates versions + associations), and the Sep-19 00:04 sync either failed to re-associate or its versions were discarded - leaving live wiring absent-or-impotent until tonight's full manual sync (1133/886/138, ER verified alive after). If AC#1 shows wiring SURVIVES a rebuild, this task reframes to: 'skill recreation orphans catalog associations; detect and trigger a full catalog sync when the live model carries no wiring but catalog versions exist' (the same detection shape as the original AC#2, but the trigger condition is orphaned-association, not rebuild-stripped).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
RESOLVED NEGATIVE by experiment (2026-09-20 03:30, live): the premise 'rebuild strips catalog wiring' is REFUTED. The AC#1 mechanical repro ran for real - live-GET of it-IT recorded the wiring (SeriesName v448, AlbumName v1018, JellyfinArtist v1012), a production single-locale rebuild ran (0 failed), and the post-rebuild live-GET shows the IDENTICAL catalogId+version triple: the PutLocaleModelPreservingWiringAsync graft (JF-552/555) preserves wiring on the rebuild path. The earlier attribution chain was wrong twice over: (1) my first live-model inspection used a broken parser (looked for valueCatalogId instead of valueSupplier.valueCatalog.catalogId), and (2) the ER outage was actually caused by the Sep-16 skill recreation orphaning the catalog associations server-side, recovered only by the full manual catalog sync (1133 artists / 886 albums / 138 series; ER_SUCCESS_MATCH verified). The wrong operational memory ('run a catalog sync after every rebuild') was deleted and replaced with the proven facts (rebuilds preserve wiring; skill RECREATION orphans associations; a full sync restores ER). Gate evidence: /simplify recorded as a no-code pass (working tree carries no task diff); code-review not applicable (no code changed). Follow-up filed as JF-597: the 65 NLU failures are UNCHANGED by the catalog recovery (66->65 across the full re-run; 987 pass) - the deterministic trainer baseline of the recreated skill, needing per-case triage (model fix vs fixture update), not an infrastructure fault.
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
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
