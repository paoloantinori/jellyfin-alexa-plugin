---
id: JF-543
title: >-
  Catalog-wired ar-SA model build fails systematically (FULL_BUILD FAILED, no
  error detail) while the embedded ar-SA model and all 16 sibling locales build
  fine
status: Done
assignee: []
created_date: '2026-09-12 03:47'
updated_date: '2026-09-12 06:38'
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
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
ROOT CAUSE FOUND AND FIXED 2026-09-12 (commit 5a31c0fc, deployed md5 53b09a32, live-verified). BISECTION RESULTS (isolated PUTs outside the sync, direct REST with real violation bodies): the failure is Amazon-side and scales with the referenced catalog's VALUE COUNT in ar-SA - artist catalog (1135 values): 0/7 builds; album catalog (895): 1/5 (the SAME payload passed once and failed 4x = per-build nondeterminism); series catalog (138): 3/3. CONTROL: de-DE + the byte-identical JellyfinArtist/artist-catalog definition = 3/3 SUCCEEDED. The embedded ar-SA model = reliable. Payload shape is correct (the live it-IT definition is field-identical); Amazon's ar-SA FULL build trainer is the defect, and it reports NO error detail. Harness notes for posterity: the v1 PUT body IS the wrapped {"interactionModel": ...} shape (bare 400s with UNEXPECTED_PROPERTY); get-interaction-model serves the last SUCCEEDED model so failed submissions must be reconstructed; ask CLI set-interaction-model ALSO wraps, do not double-wrap.

THE FIX (3 layers): CatalogManager.CatalogWiringUnsupportedLocales = {ar-SA} with the evidence in the doc comment; the sync loop filters those locales before their per-leg uploads (LibrarySyncService, with an honest exclusion log line incl. the one-time-rebuild note for pre-fix FAILED models); UpdateInteractionModelAsync refuses at the choke point (future callers); DeployCustomModelAsync rejects catalog-wired JSON for unsupported locales (the custom-model/deploy ingress). ar-SA keeps its embedded model with AMAZON.Musician slots. Tests: the set pinned (subset-of-roster + exact membership, TestLocales-driven), lookup semantics incl. case-insensitivity, and the end-to-end filter behavior on the fake-SMAPI harness (ar-SA in config -> zero ar-SA requests, it-IT leg normal). CLAUDE.md's '*' semantics updated. Gates: /simplify 4-angle clean; code-review high 6 findings all applied; suite 3656/3656.

LIVE VERIFICATION (08:14 forced sync via the minix window, post-deploy): the exclusion line fired verbatim, the sync listed 16 locales, ZERO ar-SA requests in the entire sync window, and the final skill state is 17/17 SUCCEEDED with ar-SA untouched on its embedded model (restored before the run via the rebuild endpoint). The sync itself aborted at 5/16 locales on a mid-run 401 'Token is invalid/expired' - that is the KNOWN JF-333 token-lifetime bug (refresh interval far exceeds the 1h access-token life), unrelated to this fix; the 5 legs that completed all built green and the remaining 11 wire on the next natural sync after a token refresh. Verify the 16-locale completion on the next 12h cycle (expected: all SUCCEEDED, ar-SA untouched).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Root-caused Amazon's ar-SA full-build failure to the catalog VALUE COUNT (not content or our payload shape): 1135 catalog values referenced by any catalog-backed slot type in ar-SA = 0/7 builds, 895 = 1/5 with genuine per-build nondeterminism on byte-identical payloads, 138 = 3/3, while de-DE builds 3/3 with the identical definition. The payload is field-identical to the live working it-IT one; Amazon's ar-SA trainer is the defect and reports no error detail.

Shipped a three-layer workaround: CatalogWiringUnsupportedLocales = {ar-SA} (evidence in the doc comment), enforced at the sync-loop filter (skips the locale's uploads and injection entirely, honest log line), the UpdateInteractionModelAsync choke point, and the custom-model/deploy ingress pre-flight. ar-SA keeps its embedded model with AMAZON.Musician slots, which builds and serves normally. Tests pin the set (subset-of-roster + exact membership via TestLocales), the lookup semantics, and the end-to-end filter behavior on the fake-SMAPI harness. CLAUDE.md's CatalogSyncLocales '*' semantics updated. Live-verified post-deploy: the forced sync excluded ar-SA with zero requests to it, and the skill finished 17/17 SUCCEEDED with ar-SA untouched. The verification sync aborted at 5/16 legs on the known JF-333 token-expiry bug (unrelated); the next natural sync completes the remaining catalog wiring.
<!-- SECTION:FINAL_SUMMARY:END -->
