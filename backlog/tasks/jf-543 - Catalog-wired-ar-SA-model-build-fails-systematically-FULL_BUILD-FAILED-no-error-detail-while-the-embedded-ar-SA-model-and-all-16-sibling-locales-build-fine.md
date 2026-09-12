---
id: JF-543
title: >-
  Catalog-wired ar-SA model build fails systematically (FULL_BUILD FAILED, no
  error detail) while the embedded ar-SA model and all 16 sibling locales build
  fine
status: To Do
assignee: []
created_date: '2026-09-12 03:47'
labels:
  - bug
  - smapi
  - catalog
  - ar-SA
  - nlu
dependencies:
  - JF-513
references:
  - JF-513
  - JF-513.1 (watch item origin)
  - JF-497 (the poll bookkeeping class)
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/CatalogManager.cs
    (InjectCatalogReferences)
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
SYSTEMATIC, two-failure evidence (was the JF-513.1 ar-SA watch item): the catalog sync's GetModifyPut model for ar-SA fails Amazon's LANGUAGE_MODEL_FULL_BUILD with NO error detail, while the same transformation succeeds for the other 16 locales and the EMBEDDED ar-SA model builds fine.

RUN 1: 2026-09-12 02:08-02:12 sync (the first 17-locale sync post-JF-513). ar-SA sync leg completed 106s, model PUT accepted, build -> FULL_BUILD FAILED (DIALOG_MODEL_BUILD and NAME_FREE_INTERACTION_BUILD SUCCEEDED). Restored via the embedded rebuild.

RUN 2: 2026-09-12 05:36-05:40 sync (forced early by aging LastCatalogSync via the minix_ansible_config window, per the standing delegation rule). IDENTICAL outcome: leg completed 111s, PUT accepted, FULL_BUILD FAILED with the same step signature. Restored again via the embedded rebuild (SUCCEEDED, canary OK).

DIAGNOSIS LEDGER: the payload is NOT retrievable after a failed build (get-interaction-model serves the last SUCCEEDED model), so the failing submission must be reconstructed. The transformation (CatalogManager.InjectCatalogReferences) is identical across locales: JellyfinArtist/AlbumName/SeriesName types become CatalogValueSuppliers and the 7 AMAZON.Musician musician slots re-type to JellyfinArtist. The differentiators vs the 16 working locales: (a) ar-SA's model content is Arabic, and (b) ar-SA's catalogs carry the library's names (ar-SA has NO phonetic-synonym generator; per PluginConfiguration the generators cover de/es/fr/pt/ja/nl only), so its catalog values differ from the others' only by NOT having phonetic synonyms - the 'Arabic catalog value' hypothesis is therefore WEAK. The stronger hypothesis: an interaction between ar-SA's samples and the re-typed musician slots (7 references), or something in the Arabic samples that only the FULL builder validates when a slot type is a catalog type.

NEXT DIAGNOSTIC (blind bisection, each iteration = one PUT + ~90s build, harmless overnight since this household does not use ar-SA): reconstruct the catalog-wired ar-SA payload locally (live model + InjectCatalogReferences with the current catalog ids from list-catalogs-for-skill), then bisect: (1) JellyfinArtist injection only; (2) AlbumName only; (3) SeriesName only; (4) all three with the musician re-typing reverted. Whichever variant starts building identifies the culprit half; continue halving inside it. Restore the embedded model after each FAILED iteration (custom-model/rebuild locale ar-SA). Watch for the OTHER direction too: if the reconstructed full payload BUILDES, the failure depends on sync-time state (catalog version racing), which points at JF-497-class poll/bookkeeping instead.

OPERATIONAL NOTES: every 12h sync will fail ar-SA again and leave it broken until the next manual restore - until this is fixed, either accept the twice-daily FAILED window for ar-SA (unused locale in this household) or stop-the-bleed by excluding ar-SA from CatalogSyncLocales (config; the '*' default syncs all 17).
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
