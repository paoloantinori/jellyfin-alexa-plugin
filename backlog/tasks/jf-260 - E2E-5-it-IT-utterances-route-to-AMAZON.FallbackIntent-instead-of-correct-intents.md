---
id: JF-260
title: >-
  E2E: 5 it-IT utterances route to AMAZON.FallbackIntent instead of correct
  intents
status: Done
assignee: []
created_date: '2026-06-05 16:26'
updated_date: '2026-06-05 16:56'
labels:
  - e2e
  - nlu
  - it-IT
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**5 failing E2E tests** — all route to `AMAZON.FallbackIntent` instead of the expected intent:

1. `suona la band radiohead` → expected `PlayArtistSongsIntent`, got Fallback (SMAPI simulation error)
2. `ascolta il cantante soul coughing` → expected `PlayArtistSongsIntent`, got `SearchMediaIntent`
3. `cerca il brano bohemian rhapsody` → expected `SearchMediaIntent`, got `PlaySongIntent`
4. `mostra libri` → expected `BrowseLibraryIntent`, got Fallback
5. `trova una canzone chiamata breath` → expected `FindSongByArtistIntent`, got Fallback
6. `riprodurre brani a caso` → expected `PlayRandomIntent`, got Fallback

These are NLU routing failures in the it-IT interaction model. Some may need additional sample utterances, disambiguation samples between competing intents, or new slot values.

**Fixture**: `tests/integration/fixtures/e2e_it-IT.yaml`
**Root cause area**: it-IT interaction model template (`templates/it-IT.yaml`) — multiple intents need better samples for disambiguation

Note: some of these may be related to the recent SearchMediaIntent narrowing (JF-251) that changed how search-related intents route.
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
Fixed 6 it-IT NLU routing failures by editing the YAML interaction model template and regenerating the JSON model (942 total samples, up from ~900). Key changes: added Ascolta/Ascoltare to verb vocabulary, added artist_carrier vocabulary for disambiguation carrier phrases (band/gruppo/cantante), added concrete BrowseLibraryIntent anchors, added bare-infinitive PlayRandomIntent variants, added FindSongIntent chiamata-pattern samples, and added SearchMediaIntent brano/canzone samples.
<!-- SECTION:FINAL_SUMMARY:END -->
