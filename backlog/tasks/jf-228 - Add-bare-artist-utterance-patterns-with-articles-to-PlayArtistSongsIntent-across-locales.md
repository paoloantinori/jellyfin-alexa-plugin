---
id: JF-228
title: >-
  Add bare-artist utterance patterns with articles to PlayArtistSongsIntent
  across locales
status: Done
assignee: []
created_date: '2026-05-29 19:23'
updated_date: '2026-05-29 19:32'
labels:
  - bug
  - interaction-model
  - nlu
dependencies: []
references:
  - claudedocs/research_reddit_critique_evaluation.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

When users say natural phrases like "suonare gli strokes" or "mettere gli strokes" in Italian, Alexa's NLU routes to PlaySongIntent (bare `{song}` slot) instead of PlayArtistSongsIntent, because the artist intent only has carrier-noun patterns like "Suona brani di {musician}" — never bare artist patterns with articles.

Voice test evidence (2026-05-29):
- "chiedi a jellyfin player di suonare gli strokes" → "Non ho capito" (no intent matched)
- "chiedi a jellyfin player di mettere gli strokes" → PlaySongIntent with song="gli strokes" (wrong intent)

## Root Cause

PlayArtistSongsIntent has 241 samples in it-IT, but ALL require a carrier noun (brani, canzoni, musica, pezzo, traccia) between the verb and the {musician} slot. Meanwhile PlaySongIntent has bare patterns like "Metti {song} degli {musician}" that capture the same query.

## Fix

Add bare-artist patterns with definite articles to PlayArtistSongsIntent. The article acts as a natural disambiguator — songs are singular ("il brano") while artists can be plural ("gli Strokes").

### it-IT patterns to add (4 verbs × 2 articles × 2 forms = 16):
- `Suona gli {musician}` / `Metti gli {musician}` / `Riproduci gli {musician}` / `Pleia gli {musician}`
- `Di suonare gli {musician}` / `Di mettere gli {musician}` / `Di riprodurre gli {musician}` / `Di pleiare gli {musician}`
- `Suona i {musician}` / `Metti i {musician}` / `Riproduci i {musician}` / `Pleia i {musician}`
- `Di suonare i {musician}` / `Di mettere i {musician}` / `Di riprodurre i {musician}` / `Di pleiare i {musician}`

### Other locales need equivalent analysis:
- en-US/en-GB/en-AU/en-CA/en-IN: "play the {musician}", "put on the {musician}" (article "the" disambiguates)
- de-DE: "spiele {musician}", "die {musician}" patterns
- es-ES/es-MX/es-US: "reproduce los {musician}", "pon los {musician}"
- fr-FR/fr-CA: "joue les {musician}", "mets les {musician}"
- pt-BR: "tocar os {musician}"
- ja-JP: Japanese doesn't use articles, needs different approach
- nl-NL: "speel {musician}", "zet {musician} op"
- ar-SA, hi-IN: check with native patterns

### NLU conflict risk
Adding bare patterns WITHOUT articles (e.g., "Suona {musician}") risks conflict with PlaySongIntent's "Suona {song}". Start with article-based patterns only, then test whether bare patterns are safe per locale.

## Acceptance Criteria
- [ ] it-IT: Add gli/i article patterns to PlayArtistSongsIntent
- [ ] en-*: Add "the" article patterns (NLU test first)
- [ ] Other locales: Add equivalent article-based patterns after analysis
- [ ] NLU test fixtures updated for affected locales
- [ ] Manual voice test: "suonare gli strokes" and "mettere gli strokes" route correctly
- [ ] No NLU regression on existing E2E tests
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
## Additional Italian colloquial variant (2026-05-29)

User requested adding "mettere su" as a verb variant. This is a very common Italian colloquial expression ("mettere su gli Strokes" = "put on The Strokes"). Should be added alongside existing verbs (suona, metti, riproduci, pleia):

- `Metti su gli {musician}` / `Metti su i {musician}`
- `Di mettere su gli {musician}` / `Di mettere su i {musician}`

Note: "su" only pairs with "mettere"/"metti" forms — not with suona, riproduci, or pleia.

Consider also adding to PlaySongIntent and PlayAlbumIntent as additional verb variants:
- `metti su {song}` / `metti su il brano {song}`
- `metti su {album}` / `metti su l'album {album}`
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added bare-artist utterance patterns with articles to PlayArtistSongsIntent across all 17 locale models. it-IT got 20 new samples (gli/i articles + "metti su" colloquial), en-* got 8 each ("the" article), plus de-DE, es-*, fr-*, pt-BR, ja-JP, nl-NL, hi-IN, ar-SA patterns. Also added "metti su" to PlaySongIntent (5) and PlayAlbumIntent (2) in it-IT. Build clean, 2036 tests pass, models valid.
<!-- SECTION:FINAL_SUMMARY:END -->
