---
id: JF-949
title: >-
  Song catalog opt-out must TEAR DOWN: strip the SongTitle wiring from locale
  models, delete the SMAPI catalog, clear SongCatalogId (JF-946 review F1)
status: Done
assignee: []
created_date: '2026-11-23 06:06'
updated_date: '2026-11-24 23:11'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
gate-exempt: /simplify on the review-round delta only (the seven 6fdfbbaf fixes are review-driven: a roster restructure, a distinct outcome enum member, a bare GET, detail threading, a documented non-retry, two test-honesty fixes); the worker's own 4-agent /simplify covered the base implementation 4306f1ef; the /code-review evidence (worker high 8 findings + orchestrator medium 7 findings, all applied) is in this transcript.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped 2026-11-25 via the merge of worktree-agent-a52cc82d7ae20906c (base 4306f1ef + review round 6fdfbbaf). LibrarySyncService.TearDownSongCatalogAsync: on flag-off with a stored SongCatalogId, strips the SongTitle wiring per wiring-supported locale (the locale's own embedded static block restored verbatim via CatalogSeedEnrichment.GetEmbeddedTypeBlock, or removal for the locales that never shipped one), deletes the SMAPI catalog (404=idempotent, 502/503/504 retried; 429 deliberately not retried, matching the upload path, documented), and clears SongCatalogId ONLY after every strip and the delete succeeded (any failure keeps the id and fails the run so LastCatalogSync never advances over a half-done teardown). Strip roster = the FULL wiring-supported locale set from the embedded-model roster, independent of CatalogSyncLocales (the review's serious finding: a narrowed roster could leave a wired locale under a deleted catalog). Malformed live-model bodies are a DISTINCT blocking outcome (never alias the genuinely-static case); the strip canary GET-back blocks the delete on a still-wired model (UNVERIFIED with CanaryError). Opt-in after teardown re-syncs from scratch (fresh catalog id pinned by test). Idempotent second run. 13 teardown tests, fake SMAPI, no live calls; 6255/6255 both TFMs; merged main builds clean. Review round: 7 findings applied (the roster gap serious; the rest robustness/test-honesty). Gates: worker /simplify (4 agents) + /code-review high (8 findings, all applied incl. the original design's permanent-failure flaw for the 15 no-seed locales) in its session; orchestrator /code-review medium (7 findings, all applied in 6fdfbbaf). DoD N/A: no committed model/template/fixture/locale changes; the teardown mutates DEPLOYED models at runtime on the live skill.
<!-- SECTION:FINAL_SUMMARY:END -->
