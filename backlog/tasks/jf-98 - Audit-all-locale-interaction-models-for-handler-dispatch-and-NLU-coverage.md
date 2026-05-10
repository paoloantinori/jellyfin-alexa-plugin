---
id: JF-98
title: Audit all locale interaction models for handler dispatch and NLU coverage
status: Done
assignee: []
created_date: '2026-05-08 18:42'
updated_date: '2026-05-09 20:28'
labels:
  - testing
  - nlu
  - multi-locale
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The FallbackIntentHandler bug was language-independent (C# handler dispatch), but we should verify each locale's interaction model has proper utterance coverage for the main intents (PlayArtistSongs, PlaySong, PlayAlbum, etc.) and that SMAPI simulations resolve correctly. Check all 12 locales for: 1) PlayArtistSongsIntent utterances with catalog-backed slot types 2) Built-in intent handlers working (Yes/No/Pause/Resume) 3) Any locale-specific NLU gaps
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
## Audit Complete — All 12 Locale Interaction Models

### Coverage Matrix
- **en-US**: 50 intents, 261 samples, 7 slot types (reference)
- **it-IT**: 43 intents, 683 samples, 10 slot types (richest NLU)
- **10 other locales**: 39 intents, 144-168 samples, 4 slot types (basic template)

### Dispatch Safety
- All built-in intents (Yes/No/Pause/Resume/Stop/Cancel/Next/Previous/Shuffle/StartOver) present in all 12 locales ✅
- FallbackIntentHandler (JF-97 fix) working across all locales ✅
- Handler dispatch has no crash risk — unmatched requests return "CouldNotUnderstand" ✅

### Gaps Identified
1. **11 intents en-US-only** (never backported): AddToQueue, ClearQueue, ListQueue, PlayNext, PlayRadio, TurnRadioOn/Off, LearnMyVoice, QueryArtistLibrary, WhoAmI, PlayByDecade
2. **3 slot types missing** in 10 locales: Decade, TimePeriod, LibraryQueryType
3. **Critical NLU quality gap**: PlayArtistSongs/PlaySong/PlayAlbum have only 2 samples in 11 locales (vs 76-236 in it-IT) — Alexa NLU will struggle to match these

### Looping Strategy (by design, not a bug)
- English/Spanish locales: AMAZON.LoopOn/Off built-ins + LoopSongOn custom
- de-DE/fr-CA/fr-FR/it-IT: LoopAllOn/Off + RepeatSingleOn custom (correct — locales without built-in loop support)

No blocking issues for handler dispatch. NLU quality gaps are a separate improvement task.
<!-- SECTION:FINAL_SUMMARY:END -->
