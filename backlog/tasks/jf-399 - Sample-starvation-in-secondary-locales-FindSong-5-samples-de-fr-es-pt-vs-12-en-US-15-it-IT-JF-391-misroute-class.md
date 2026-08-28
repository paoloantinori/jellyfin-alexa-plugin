---
id: JF-399
title: >-
  Sample starvation in secondary locales: FindSong 5 samples (de/fr/es/pt) vs 12
  en-US / 15 it-IT - JF-391 misroute class
status: In Progress
assignee: []
created_date: '2026-08-23 05:56'
updated_date: '2026-08-23 10:09'
labels:
  - nlu
  - interaction-model
  - localization
milestone: m-16
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Primary finding of the 2026-08-23 model audit. Sample counts per intent vs en-US (12) and it-IT (15): FindSongIntent has only 5 samples in pt-BR, es-ES, es-MX, fr-FR, fr-CA, de-DE (2 slotted variants only). PlaySongIntent: ja-JP 10 and hi-IN 14 vs en-US 38. PlayPlaylistIntent: ja-JP 5 vs 20. This is the same class of imbalance that caused JF-391 (playlist requests misrouted to the album intent because 12 samples lost to 354): NLU preferentially matches the intent with more samples, so these locales likely misroute conversational song-search requests to other intents.

Plan: (1) reproduce the misroute with profile-nlu for a de-DE "finde ein lied" style utterance to confirm impact; (2) raise FindSongIntent + PlaySongIntent to >= 12 samples in de/fr/es/pt first (biggest gap), then ja/hi/other; (3) add NLU fixtures for the new utterances in the covered locales. Anti-pattern #4 rules apply: same samples across all locales simultaneously once the set is agreed.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Scope note 2026-08-23: parity goes BOTH directions - en-US itself is relatively poor (523 total samples vs 1250 it-IT; MediaInfoIntent 47 is its richest custom intent vs 354 PlayAlbum it-IT). While fixing the starved locales, also assess whether en-US top play-intents (PlaySong 38, PlayArtistSongs 34) deserve the carrier-noun expansion it-IT got, since en-US is the reference locale for most external users.

2026-08-23 shipped (commit 959dfcc + DLL deploy + model redeploy): FindSongIntent enriched to 14-16 samples in de-DE/fr-FR/fr-CA/es-ES/es-MX/pt-BR. Live-verified on profile-nlu: the de-DE PlaySongIntent misroute ('ich will ein lied finden namens lange nacht') and the fr/es keyword captures now route correctly. BONUS fix discovered by the NLU suite: de-DE mood inflections ('trauriges'/'fröhliche') misrouted to PlayByGenreIntent; added inflected synonyms to the de mood table, regenerated, live-verified PlayMoodMusicIntent now wins.

NOT deployable on the current vendor skill: pt-BR is not among the 12 active locales of this skill (rebuild returns empty); the pt-BR model file is correct in the repo and will apply where a user has pt-BR active.

Still open in this task: PlaySongIntent ja-JP (10) / hi-IN (14) bump vs en-US 38, PlayPlaylistIntent ja-JP (5); residuals moved to JF-405 (de 'mit X im titel' no-intent, pt 'sobre o mar' keyword miss, fr 'je cherche une chanson sur la pluie' misroute).

Rebuild-endpoint oddity observed (not blocking): custom-model/rebuild with no locale falls back to CustomModelLocale (en-US) instead of rebuilding all; the all-locales option is the open JF-348.

BLOCKER for the ja-JP/hi-IN (and nl/pt/ar) portion: those locales are NOT active on the current dev skill (profile-nlu returns 400; rebuild skips them), so enrichment there cannot be live-verified. Shipping unverified Japanese/Hindi samples is high-risk. Proposal for the user: temporarily enable ja-JP (and hi-IN) on the dev skill via the plugin's skill management, then enrich + verify + disable again. Until then this portion stays open.
<!-- SECTION:NOTES:END -->
