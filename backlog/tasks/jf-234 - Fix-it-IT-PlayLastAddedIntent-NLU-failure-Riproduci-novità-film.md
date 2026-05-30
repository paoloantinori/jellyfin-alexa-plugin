---
id: JF-234
title: Fix it-IT PlayLastAddedIntent NLU failure ("Riproduci novità film")
status: Done
assignee: []
created_date: '2026-05-30 08:43'
updated_date: '2026-05-30 10:09'
labels: []
dependencies:
  - JF-231
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
One it-IT utterance fails to resolve to PlayLastAddedIntent:
- "Riproduci novità film" → expected PlayLastAddedIntent with media_type slot

Investigate what intent the NLU actually routes this to (likely PlaySongIntent or BrowseLibraryIntent), then add disambiguating utterance samples to model_it-IT.json for PlayLastAddedIntent, or adjust the test fixture if acceptable.

Fixture: tests/integration/fixtures/it-IT.yaml line 522
Model: Alexa/InteractionModel/model_it-IT.json
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
Fixed by adding "film" value (with synonyms "movies", "cinema") to the MediaType slot type in model_it-IT.json. NLU can now resolve "Riproduci novità film" to PlayLastAddedIntent.
<!-- SECTION:FINAL_SUMMARY:END -->
