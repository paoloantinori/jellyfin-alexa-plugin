---
id: JF-232
title: Fix it-IT PlayByGenreIntent NLU failures (2 utterances)
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
Two it-IT utterances fail to resolve to PlayByGenreIntent:
- "Riproduci genere rock" → expected PlayByGenreIntent with genre slot
- "Suona genere jazz" → expected PlayByGenreIntent with genre slot

Investigate what intent the NLU actually routes these to (likely PlaySongIntent or another dominant intent), then add disambiguating utterance samples to model_it-IT.json for PlayByGenreIntent, or adjust the test fixture if the NLU routing is acceptable.

Fixture: tests/integration/fixtures/it-IT.yaml lines 74-82
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
Fixed by adding 8 "genere {genre}" anchor samples to PlayByGenreIntent in model_it-IT.json. Both "Riproduci genere rock" and "Suona genere jazz" now resolve correctly.
<!-- SECTION:FINAL_SUMMARY:END -->
