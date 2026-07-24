---
id: JF-169
title: Fix it-IT PlayLastAddedIntent AMAZON.SearchQuery coexistence violation
status: Done
assignee: []
created_date: '2026-05-17 13:46'
updated_date: '2026-05-17 14:10'
labels:
  - bug
  - interaction-model
  - it-IT
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The `validate_interaction_models.py` script reports a structural error in it-IT:

```
[it-IT] Intent 'PlayLastAddedIntent': slot 'time_period' references undefined slot type 'TimePeriod'
[it-IT] Intent 'PlayLastAddedIntent': AMAZON.SearchQuery cannot coexist with other slots (['time_period'])
```

PlayLastAddedIntent has both an AMAZON.SearchQuery slot and a `time_period` slot. Alexa's NLU does not allow AMAZON.SearchQuery to coexist with other slot types in the same utterance. This is the only structural model error blocking `validate-models` from going green.

Fix options:
1. Remove the `time_period` slot and rely on SearchQuery only
2. Replace AMAZON.SearchQuery with a custom slot type and keep time_period
3. Add the missing TimePeriod slot type definition

This is the last blocker for making `validate-models` a hard CI gate.
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
Fixed it-IT PlayLastAddedIntent SearchQuery coexistence violation:
- Changed media_type slot from AMAZON.SearchQuery to MediaType across 4 intents (PlayLastAddedIntent, PlayFavoritesIntent, PlayRandomIntent, RecommendIntent)
- Added MediaType slot type with Italian values (media, video, musica with synonyms canzoni/brani)
- Added TimePeriod slot type with Italian values (oggi, questa settimana, questo mese)
- Added MediaInfoType slot type with Italian values (titolo, album, artista, anno, durata, genere, biografia)
- validate_interaction_models.py now reports PASS with zero structural errors
<!-- SECTION:FINAL_SUMMARY:END -->
