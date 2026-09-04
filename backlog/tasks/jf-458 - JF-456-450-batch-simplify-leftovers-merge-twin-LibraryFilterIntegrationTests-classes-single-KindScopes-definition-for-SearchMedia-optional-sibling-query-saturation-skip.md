---
id: JF-458
title: >-
  JF-456/450 batch simplify leftovers: merge twin LibraryFilterIntegrationTests
  classes, single KindScopes definition for SearchMedia, optional sibling-query
  saturation skip
status: Done
assignee: []
created_date: '2026-09-02 17:08'
updated_date: '2026-09-04 21:29'
labels:
  - tech-debt
  - library-filter
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/LibraryFilterIntegrationTests.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/SearchMediaIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the batch simplify round over JF-456+JF-450/451 (2026-09-02), deliberately not applied in-batch to contain the pre-deploy diff. 1) Twin test classes: Jellyfin.Plugin.AlexaSkill.Tests/Handler/LibraryFilterIntegrationTests.cs (235 lines) and Tests/Unit/LibraryFilterIntegrationTests.cs (484 lines) share the class name, both test handler library-filter behavior, both received the same membership-assert rewrite, and the Handler copy re-inlines the CaptureAllQueriesViaLibraryManager helper the Unit copy extracted; fold the Handler file's PlaySong tests and the sibling-query assertion into the Unit file and delete one (ambiguous test output today). 2) SearchMedia scope-split expressed twice with different mechanics: the primary path's branch method SearchPlayableKindsAsync vs the fuzzy path's ternary plus conditional second call (SearchMediaIntentHandler.cs ~147-153 vs ~224-288); a tiny KindScopes(libraryRestricted) helper returning the one-or-two kind arrays would let both paths consume one definition. 3) Informational: the restricted-user sibling playlist query has no skip condition and none is derivable from content gating (Playlist is hard-allowed by FilterByContentAccess); a saturation short-circuit (skip sibling when scoped.Count >= Limit) saves one DB roundtrip per restricted search but changes edge-case ordering of the re-sorted union; adopt only with a documented ordering decision.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
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
2026-09-04 (executed):

1. MERGE DONE. Tests/Handler/LibraryFilterIntegrationTests.cs is deleted; its PlaySong tests (restricted, null AllowedLibraryIds, empty AllowedLibraryIds) and the sibling-query assertion were folded into Tests/Unit/LibraryFilterIntegrationTests.cs (which keeps the name and file location) in that file's established style: membership assertions via the already-extracted capture helpers, GetItemById folder stubs, Mock.Of IUserDataManager handler construction. The null/empty ports assert All-captured-queries instead of the old last-query-only capture (stronger and cascade-proof; every PlaySong query is library-scoped so the unrestricted no-op filter leaves them all empty). The sibling assertion lives in SearchMedia_RestrictedUser_SiblingPlaylistQueryHasNoTopParentIds with its found-movie setup kept local (the shared capture helpers return empty lists; the threshold-above-fallback cleanliness needs a hit). Net test count unchanged: 4 tests moved, 0 added.

2. KINDSCOPES DONE. One private (Primary, Sibling) KindScopes(bool libraryRestricted) definition in SearchMediaIntentHandler returns (_libraryScopedPlayableTypes, _outOfLibraryPlayableTypes) under a restriction and (_playableTypes, null) otherwise. SearchPlayableKindsAsync destructures it once (Primary feeds the unified query and the scoped query, Sibling the playlist sibling query); the fuzzy pass in HandleAsync destructures the same helper for its primary array and its conditional second call (the old ternary plus the libraryRestricted recheck). FilterByContentAccess wrapping stays at the primary-path call sites and SearchItemsFuzzyAsync's internal kind-aware ApplyLibraryFilter keeps serving the fuzzy path, exactly as before; only the WHICH-kinds-in-WHICH-scopes decision is now single-sourced.

3. SATURATION SKIP: no change needed; it was adopted while this task sat filed. Commit d02b9300 (JF-456, 2026-09-02 21:35, four hours after this task's 17:08 creation) introduced the skip in SearchPlayableKindsAsync ("Saturation skip (code-review round 2 item 3)": query the sibling only when scoped.Count < limit, with the scopedTypes.Length == 0 escape so the sibling still runs when it is the only permissible query) AND documented the re-sorted-union ordering decision in place (client-side Name re-sort of the union plus the re-cap at the same Limit, with the SortName-vs-Name divergence note, code-review F5/F8). The fuzzy sibling already short-circuits on fuzzy == null. Decision: nothing further to adopt.

Verification: dotnet build 0 warnings 0 errors; full suite 3242/3242 passed (a CONCURRENT session's uncommitted JF-457/JF-491 work shares this checkout with +7 of its own new tests; its ArtistSearchTests.DbTier1 test was mid-work and failing during this task's runs, not involving any of this diff's code paths, and passed green by the final run).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented and deployed 2026-09-04 (commit e14beecc, together with JF-442). Twin LibraryFilterIntegrationTests classes merged into the Unit copy (4 tests moved with the sibling-query assertion verbatim; null/empty AllowedLibraryIds variants strictly stronger via Assert.All over every captured query; the Handler file deleted, ambiguous test output resolved). One KindScopes(libraryRestricted) definition in SearchMediaIntentHandler consumed by both the primary path and the fuzzy pass. Item 3 resolved as already-landed: the sibling-query saturation skip shipped in d02b9300 (JF-456, 2026-09-02) with the documented re-sorted-union ordering decision; nothing to adopt. Full suite 3243/3243.
<!-- SECTION:FINAL_SUMMARY:END -->
