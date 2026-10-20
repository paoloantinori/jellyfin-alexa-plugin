---
id: JF-915
title: >-
  JF-915: the Nora Jones wrapper regression (mood-steal of 'mettere musica di X'
  ate the artist, 10-artist choice with Tom first) fixed by the four
  bare-infinitive musica rows
status: In Progress
assignee: []
created_date: '2026-10-20 05:58'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
REGRESSION REPORT (Paolo, 2026-10-20 ~07:21 device session): "Chiedi a mia collezione di mettere musica di Nora Jones" answered the 10-artist multiple choice "Ho trovato 10 artisti: Tom Jones, Norah Jones, Danko Jones. Vuoi il primo?" (Tom Jones first, the wrong artist) and the session then died EXCEEDED_MAX_REPROMPTS.

CAUSAL CHAIN (verified link by link; device corr 15656cee + simulate reproduction): (1) the device delivered the request to PlayMoodMusicIntent with slot mood='jones' (request body read from podman logs; the 'di nora' prefix was eaten by the statistical slot fill). (2) ROOT: the invocation-consumed wrapper tail 'mettere musica di ...' had NO bare-infinitive row on PlayArtistSongsIntent (the {infinitive} vocabulary is Di+verb only), while PlayMoodMusicIntent carries the bare 'Mettere musica {mood}' - so the mood side won the selection race and its fill swallowed the artist prefix. (3) The mood-miss fallback (the designed JF-354 recovery) then searched the bare 'jones', the 4-tier fuzzy chain returned the 10-candidate Jones family with Tom Jones rank-1, and DisambiguateMultipleArtists fired. (4) ESCAPE HATCH SPENT: one identical-content it-IT rebuild flipped the steal to FallbackIntent without recovery (2026-10-20 08:2x), so the model-side row fix was required.

FIX (the JF-844 class, fourth occurrence): four bare-infinitive literal rows on PlayArtistSongsIntent ('riprodurre/suonare/mettere/ascoltare musica {artist_article} {musician}', the existing article vocabulary expanding to 16 samples), mirroring the PlaySong/PlayEpisode precedents. Fixture pins: the incident one-shot + bare payload + the suonare/dei sibling twin pair (wrapper + bare) + the mood-side reverse guard ('mettere musica rilassante' stays mood). e2e row: the exact wrapper phrase with the AudioPlayer.Play directive expectation (RED-UNTIL-DEPLOY family). The bare imperative was never broken (verified: 'metti musica di nora jones' routes musician='nora jones'); Norah Jones resolves handler-side from the raw fill through the phonetic chain.
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
