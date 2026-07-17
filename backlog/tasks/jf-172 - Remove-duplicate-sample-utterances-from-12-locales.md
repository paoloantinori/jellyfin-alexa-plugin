---
id: JF-172
title: Remove duplicate sample utterances from 12 locales
status: Done
assignee: []
created_date: '2026-05-17 13:47'
updated_date: '2026-05-17 14:10'
labels:
  - interaction-model
  - tech-debt
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
12 locales have duplicate sample utterances in their interaction models. Alexa deduplicates silently, but duplicates waste slot budget and indicate copy-paste errors.

Affected locales and intents:
- ar-SA: PlayByGenreIntent, LearnMyVoiceIntent
- en-AU/en-CA/en-IN: PlayFavoritesIntent, PlayEpisodeIntent
- en-GB: PlayEpisodeIntent
- en-US: PlayLastAddedIntent, PlayFavoritesIntent, PlayEpisodeIntent
- es-ES/es-MX/es-US: PlayMoodMusicIntent
- hi-IN: PlayByGenreIntent, MarkFavoriteIntent, ListQueueIntent, SearchMediaIntent (2x)
- nl-NL: MarkFavoriteIntent, LearnMyVoiceIntent
- pt-BR: PlaySongIntent, MediaInfoIntent, SearchMediaIntent

Fix: remove the duplicate entry from each intent's samples array. The `validate_interaction_models.py` script reports these as warnings.
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
Removed 25 duplicate sample utterances across 12 locales:
ar-SA (2), en-AU (2), en-CA (2), en-GB (1), en-IN (2), en-US (3), es-ES (1), es-MX (1), es-US (1), hi-IN (5), nl-NL (2), pt-BR (3)
Duplicate warnings reduced from 20 to 0. Total model warnings dropped from 97 to 58.
<!-- SECTION:FINAL_SUMMARY:END -->
