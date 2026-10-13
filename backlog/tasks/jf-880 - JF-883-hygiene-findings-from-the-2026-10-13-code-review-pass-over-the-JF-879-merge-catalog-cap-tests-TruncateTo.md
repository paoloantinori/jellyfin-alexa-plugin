---
id: JF-880
title: >-
  JF-883: hygiene findings from the 2026-10-13 code-review pass over the JF-879
  merge (catalog cap tests + TruncateTo)
status: Done
assignee: []
created_date: '2026-10-13 09:31'
updated_date: '2026-10-13 12:00'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Findings from a /code-review high pass (2026-10-13) that accidentally targeted the JF-879 merge range instead of the then-open JF-876 diff; the findings are real and need a home. All hygiene-level, in Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/CatalogPayload.cs and the two cap test files:
1. LibrarySyncServiceCatalogCapTests.cs:213 marker-based payload lookup relies on a shared static cache needing manual ClearStoredPayloads() per pin; a forgotten clear resolves a sibling test's payload (order-sensitive global). Scope the stored payload per test or use a unique injected marker.
2. The JF-848/JF-879 reserve-on-any-cut policy narrative is retold in 4+ places (TruncateTo XML summary + returns doc, LibrarySyncService pre-warning comment, five test doc comments); the merge itself fixed 3 doc-rot instances. Keep the canonical rationale at TruncateTo, one-line pointers elsewhere.
3. The truncation-warning layout string is duplicated as inline Assert.Contains literals in six-plus tests, unlinked to the LibrarySyncService.cs:1162 template; add an ExpectedWarning(count, staticCount, libraryCount) helper.
4. CatalogPayload.cs:116 TruncateTo materializes the dropped block twice (GetRange copy + RemoveRange shift) and returns up to ~50k references whose consumer only needs counts and 3 names; derive counts arithmetically and sample before removal.
5. CatalogPayloadTruncateToTests.cs:57 redundant head-triplet assertion that breaks on seed-count changes; the file mixes fully-qualified System.Guid with imported System.Linq.
6. CatalogPayloadTruncateToTests.cs:112 post-hoc mutation of Values[^1].Name.Value instead of building the generic-word tail entry through CatalogValueFactory.Create, so the pin can silently diverge from real factory output.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf883 (871b3c78), merged to main c0608871, pushed. Per-finding disposition (each verified against the merged 9227dd04 first): (1) APPLIED at deeper altitude, the stored-payload cache clear now lives in PluginTestBase.ResetSharedStatics (the ONE shared-static reset owner; Plugin collection is DisableParallelization so no hazard); (2) rationale deduplicated to TruncateTo's XML summary with pointers, the JF-825 failure-mode rationale RESTORED at its enforcement site after the review caught the trim deleting it; (3) ExpectedDropComposition helper replaces the 7 inline literals, es-ES pin upgraded to the full phrase; (4) NOT APPLIED, superseded with evidence: the ~50k-dropped premise is false in the merged state (the fetch bound caps the entire drop at staticAppendedCount; the redesign would flip pin semantics); (5) applied WITH A REVERSAL: the review proved the head-triplet pin load-bearing, so it became a derived FULL kept-head pin, strictly stronger; (6) the generic-word tail rides CatalogValueFactory.Create (JF-689 one-construction), no post-hoc mutation. Gates: /simplify (3 applied, 2 reasoned skips) + /code-review high (4/4 verified and applied). Tests 5911/5911 BOTH TFMs; build 0 warnings; production changes comment-only. No deploy needed (test-hygiene + comments).
<!-- SECTION:FINAL_SUMMARY:END -->
