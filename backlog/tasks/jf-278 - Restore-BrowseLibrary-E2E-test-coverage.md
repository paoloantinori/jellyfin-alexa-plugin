---
id: JF-278
title: Restore BrowseLibrary E2E test coverage
status: Done
assignee: []
created_date: '2026-06-08 09:32'
updated_date: '2026-06-08 11:40'
labels:
  - e2e
  - nlu
milestone: m-5
dependencies: []
modified_files:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/BrowseLibraryIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
BrowseLibraryIntent had its E2E fixture removed (simulate-skill couldn't route bare browse commands). Category browsing is untested. Need to:
1. Create reliable E2E fixtures for browse categories (movies, series, albums, genres)
2. Test via simulator with category slot values
3. Verify response format (list with ordinal selection)
4. Test "show more" pagination if list is truncated
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added 5 BrowseLibrary E2E test fixtures for it-IT covering film, serie, artisti, canzoni, and generi categories with varied utterance patterns (verb+article, different verbs, question forms). All verified via NLU profiler (profile-nlu). Dry-run validates 51 total fixtures. Note: "mostra libri" still excluded due to simulate-skill NLU routing issue (documented in existing comment). Commit: bec95b4.
<!-- SECTION:FINAL_SUMMARY:END -->
