---
id: JF-557
title: >-
  BrowseLibrary genre path unreachable: filter slot undeclared in all 17 models,
  so genre browsing always falls into the category question
status: To Do
assignee: []
created_date: '2026-09-13 11:21'
labels:
  - bug
  - interaction-model
dependencies: []
references:
  - JF-550
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/BrowseLibraryIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found during the JF-550 review (2026-09-13), pre-existing and out of that sweep's scope. BrowseLibraryIntentHandler reads a "filter" slot (BrowseLibraryIntentHandler.cs:97) and its genre path uses it as the genre name (HandleGenresQuery: Genres = new[] { filter }), but NO model declares a filter slot for BrowseLibraryIntent in ANY of the 17 locales (verified: model slot set is [browse_category] only everywhere). Alexa never sends undeclared slots, so filter is always null and the genre path ALWAYS falls into the DidNotCatchBrowseCategory question. Consequences: (a) "browse genres"/"sfoglia i generi" is a dead end - the user is asked for a category and any genre answer ("rock") cannot resolve into the BrowseCategory custom type either, so the only escapes are naming a different category or a cancel; (b) the post-elicit genre listing code (Genres = [filter] query) is dead in production. Fix directions to evaluate: declare a filter slot (genre-shaped custom type or SearchQuery constraints permitting) with carriers like "browse {browse_category} {filter}", or drop the genre branch and route genre words to PlayByGenre/PlayMoodMusic vocabulary. Note the JF-550 e2e/unit pins cover the elicit shape, not this semantic dead end.
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
