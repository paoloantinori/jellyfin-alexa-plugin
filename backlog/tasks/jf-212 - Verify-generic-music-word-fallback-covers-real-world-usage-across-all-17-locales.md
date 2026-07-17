---
id: JF-212
title: >-
  Verify generic music word fallback covers real-world usage across all 17
  locales
status: Done
assignee: []
created_date: '2026-05-25 06:05'
updated_date: '2026-05-25 10:46'
labels:
  - locale
  - nlu
  - improvement
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The PlaySongIntentHandler now has a `GenericMusicWords` set that triggers artist-songs fallback when the song slot contains a generic word like "musica"/"music" instead of a real song title. Current list covers English, Italian, German, Spanish, French, Dutch, Portuguese — but may be incomplete.

Check each of the 17 supported locales for common colloquial phrases that mean "play music by X" where Alexa might capture a generic word into the {song} slot:
- Review PlayArtistSongsIntent utterances in each locale to find carrier words used before {musician} (e.g. "musica", "brani", "chansons")
- Cross-reference with PlaySongIntent utterances to identify NLU competition patterns
- Add any missing generic words to the `GenericMusicWords` set
- Consider locale-specific colloquialisms that ASR might produce (e.g. "música" with/without accent, phonetic variants)

Locales: en-US, en-GB, de-DE, es-ES, fr-FR, it-IT, ja-JP, pt-BR, es-MX, fr-CA, en-IN, en-AU, es-US, hi-IN, pt-PT, nl-NL, en-CA
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
Expanded GenericMusicWords from 14 to 38 words across 8 languages. Added singular forms (brano, chanson, lied), accented/unaccented variants (canción/cancion, músicas/musicas), and new nouns (pezzo, morceau, titel, tema, faixa). Added 4 unit tests verifying completeness, case-insensitivity, and exclusion of structural words. Build 0 errors, 1879 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
