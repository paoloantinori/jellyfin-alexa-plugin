---
id: JF-170
title: 'Backport missing intents to it-IT, de-DE, fr-CA, fr-FR'
status: Done
assignee: []
created_date: '2026-05-17 13:46'
updated_date: '2026-05-17 14:10'
labels:
  - interaction-model
  - i18n
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
it-IT is missing 10 intents that all other locales have (present in 16/17 locales):
- AddToQueueIntent
- ClearQueueIntent
- FollowMeIntent
- ListQueueIntent
- LoopSongOnIntent
- PlayNextIntent
- PlayRadioIntent
- QueryRecentlyAddedIntent
- TurnRadioOffIntent
- TurnRadioOnIntent

Additionally, de-DE, fr-CA, fr-FR are missing LoopSongOnIntent (present in 13/17 locales).

These require:
- Intent definitions with Italian/German/French sample utterances in the respective interaction models
- Any missing custom slot types
- Locale response strings for any new response keys

The `validate_interaction_models.py` cross-locale check reports these as warnings. Once fixed, update the baseline and make validate-models a hard CI gate.
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
Backported missing intents:
- Added 10 intents to it-IT: AddToQueueIntent, ClearQueueIntent, FollowMeIntent, ListQueueIntent, LoopSongOnIntent, PlayNextIntent, PlayRadioIntent, QueryRecentlyAddedIntent, TurnRadioOnIntent, TurnRadioOffIntent
- Added LoopSongOnIntent to de-DE (German), fr-CA (French Canadian), fr-FR (French)
- All it-IT intents include phonetic variants (Pleia/pleiare) and formal forms (Di + infinitive)
- Locale response strings already present in all target locales
- Cross-locale missing intent warnings eliminated
<!-- SECTION:FINAL_SUMMARY:END -->
