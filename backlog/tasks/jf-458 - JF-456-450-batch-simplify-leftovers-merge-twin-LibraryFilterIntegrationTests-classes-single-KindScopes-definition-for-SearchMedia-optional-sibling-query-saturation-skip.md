---
id: JF-458
title: >-
  JF-456/450 batch simplify leftovers: merge twin LibraryFilterIntegrationTests
  classes, single KindScopes definition for SearchMedia, optional sibling-query
  saturation skip
status: To Do
assignee: []
created_date: '2026-09-02 17:08'
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
