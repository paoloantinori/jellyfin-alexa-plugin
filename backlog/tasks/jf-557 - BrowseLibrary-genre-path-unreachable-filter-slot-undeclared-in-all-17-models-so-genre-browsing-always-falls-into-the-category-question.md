---
id: JF-557
title: >-
  BrowseLibrary genre path unreachable: filter slot undeclared in all 17 models,
  so genre browsing always falls into the category question
status: To Do
assignee: []
created_date: '2026-09-13 11:21'
updated_date: '2026-09-13 11:36'
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

## Implementation Notes

FIXED 2026-09-13 (option a from the task: declare the filter slot). All 17 templates: BrowseLibraryIntent gains the `filter` slot (AMAZON.SearchQuery - legal coexistence with browse_category because no single sample carries both, anti-pattern #2) plus two locale-native genre carriers ("browse genres {filter}" / "mostra i generi {filter}" / "تصفح أنواع {filter}" ...) mirroring each locale's own browse verbs; the dialog entries gain the filter slot with elicitationRequired:false (Phase 8 slot-parity held: the checker CAUGHT the handler's allSlotNames missing "filter" during development - first real catch of the JF-556 check - fixed to "browse_category","filter"). Handler: carrier-shaped requests (browse_category empty, filter filled - the literal category never fills the slot) route to the genre path BEFORE the missing-category Ask, resolving the user inline so the Ask stays user-resolution-free (two test-pinned regressions caught and fixed during development). The validator Phase 8 parity now covers the new slot; the dead-mic detector stays at 0; 2 new unit tests (genre-with-category queries Genres; filter-only carrier shape runs the genre query). Suite 3698/3698 both TFMs. LIVE-VERIFIED 2026-09-13 17:07: models rebuilt (it-IT/en-US SUCCEEDED), profile-nlu routes "mostra i generi rock" -> BrowseLibraryIntent filter=rock, and the simulator returns the real genre listing ("Ho trovato 15 rock. Ecco i primi 5: ..." with the filter-only genre-browse log line). The routing addendum (bare playlist form -> BrowseLibrary/Fallback competition) remains context for any future browse-carrier work, not an open item here.

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
ROUTING ADDENDUM (2026-09-13, JF-550 live probes): the it-IT bare empty-name playlist one-shot ('riprodurre la playlist', simulate-skill) routes to BrowseLibraryIntent with browse_category resolving ER_SUCCESS to the it-IT 'playlist' browse concept, and the final selection falls to FallbackIntent - it never reaches PlayPlaylistIntent, so the playlist elicit cannot fire via this phrasing (every PlayPlaylistIntent carrier anchors on {playlist} itself). Same shape for SleepTimerIntent: all four it-IT carriers embed {duration_minutes}, so no slot-less routing exists. Empty-primary-slot arrivals for the swept intents are therefore mostly the elicit's own IN_PROGRESS answering turns plus rare partial matches (the JF-549 PlayEpisode incident was the anchor-slot case: numbers filled, series empty). Kept as routing-competition context for this task's filter-slot fix.
<!-- SECTION:NOTES:END -->
