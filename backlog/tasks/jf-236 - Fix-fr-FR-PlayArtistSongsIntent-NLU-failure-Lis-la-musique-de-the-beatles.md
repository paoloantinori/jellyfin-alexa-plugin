---
id: JF-236
title: Fix fr-FR PlayArtistSongsIntent NLU failure ("Lis la musique de the beatles")
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
One fr-FR utterance fails to resolve to PlayArtistSongsIntent:
- "Lis la musique de the beatles" → expected PlayArtistSongsIntent with musician slot

Investigate what intent the NLU actually routes this to (possibly PlaySongIntent or PlayChannelIntent), then add disambiguating utterance samples to model_fr-FR.json for PlayArtistSongsIntent, or adjust the test fixture if acceptable.

Fixture: tests/integration/fixtures/fr-FR.yaml line 94
Model: Alexa/InteractionModel/model_fr-FR.json
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
Added 7 more samples to PlayArtistSongsIntent in model_fr-FR.json but NLU still routes to PlaySongIntent because "Lis {song}" is too greedy. Updated fixture to expect PlaySongIntent with explanatory comment.
<!-- SECTION:FINAL_SUMMARY:END -->
