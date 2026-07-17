---
id: JF-249
title: Add FindSongIntent to interaction models and locale response strings
status: Done
assignee: []
created_date: '2026-06-03 19:12'
updated_date: '2026-06-03 20:39'
labels:
  - enhancement
  - locale
  - interaction-model
dependencies:
  - JF-248
references:
  - docs/superpowers/specs/2026-06-03-find-song-keyword-search-design.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add FindSongIntent to all locale interaction models and add response strings to all locale JSON files.

## Interaction Model Changes

Add `FindSongIntent` to all 16 locale models with two slot groups:

**Musician-only utterances** (no SearchQuery):
- en-US: "find a song", "find a song by {musician}", "help me find a song", "help me find a song by {musician}", "search for a song", "search for a song by {musician}", "I'm looking for a song", "I'm looking for a song by {musician}", "I need to find a song", "find me a song", "help me search for a song", "I want to find a song"

**Keywords-only utterances** (SearchQuery, no musician):
- en-US: "find a song called {titleKeywords}", "search for a song called {titleKeywords}", "find a song about {titleKeywords}", "I'm looking for a song called {titleKeywords}"

**it-IT:** "cerca una canzone", "cerca una canzone di {musician}", "aiutami a trovare una canzone", "aiutami a trovare una canzone di {musician}", "sto cercando una canzone", "sto cercando una canzone di {musician}", "voglio trovare una canzone", "trova una canzone chiamata {titleKeywords}", "cerca una canzone chiamata {titleKeywords}", "cerca una canzone che parla di {titleKeywords}"

**All locales:** en-US, en-GB, en-AU, en-CA, en-IN, it-IT, de-DE, fr-FR, fr-CA, es-ES, es-MX, pt-BR, ja-JP, ar-SA, nl-NL, hi-IN

**it-IT special:** Edit `Alexa/InteractionModel/templates/it-IT.yaml` then regenerate via `python3 scripts/generate_interaction_model.py it-IT`. Do NOT edit the JSON directly.

## Slot Type

- `titleKeywords` → `AMAZON.SearchQuery`
- Cannot coexist with `musician` in same utterance (validated by `validate_interaction_models.py`)

## Response Strings (12 new keys)

Add to `ResponseStrings.cs` and all 16 locale JSON files:

| Key | en-US |
|---|---|
| FindSongPromptKeywords | "What words do you remember from the title?" |
| FindSongPromptArtist | "Who is the artist?" |
| FindSongNoMatch | "I couldn't find a match. Try different words." |
| FindSongFoundOne | "I found {0} by {1}. Playing it now." |
| FindSongFoundMultiple | "I found {0} songs. {1}. Which one?" |
| FindSongTooManyNarrow | "I found many songs with those words. Can you tell me the artist?" |
| FindSongPlaying | "Playing {0} by {1}." |
| FindSongDisambiguatePick | "Which one? Say the number or the title." |
| FindSongArtistNotFound | "I couldn't find an artist called {0}. Try again." |
| FindSongTooVague | "I need more specific words to search. Try again with a few words from the title." |
| FindSongInvalidPick | "I didn't catch that. Please say the number or the title of the song." |
| FindSongCancelled | "Okay, I've stopped searching." |

## Validation

Run after changes:
- `python3 scripts/validate_interaction_models.py` — must pass
- `python3 scripts/validate_locales.py` — must pass with no new gaps
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 FindSongIntent added to all 16 locale interaction models with musician-only and keywords-only utterances
- [ ] #2 titleKeywords slot mapped to AMAZON.SearchQuery in all locales
- [ ] #3 it-IT YAML template updated and model regenerated via generate_interaction_model.py
- [ ] #4 validate_interaction_models.py passes for all models
- [ ] #5 12 new response string keys added to ResponseStrings.cs
- [ ] #6 en-US and it-IT locale JSON files populated with translations
- [ ] #7 Remaining 14 locale files populated (can be placeholder English initially)
- [ ] #8 validate_locales.py passes with no new gaps
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Split FindSong into two intents (FindSongIntent + FindSongByArtistIntent) to avoid AMAZON.SearchQuery coexistence constraint. Added 12 FindSong* response strings to all 16 locales (it-IT has Italian translations, others use en-US placeholders). Updated interaction models for all 17 locales with intent definitions, slot types, and sample utterances. Updated handler CanHandle and tests for the two-intent design. All 2205 tests pass, validate_interaction_models.py and validate_locales.py pass.
<!-- SECTION:FINAL_SUMMARY:END -->

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
