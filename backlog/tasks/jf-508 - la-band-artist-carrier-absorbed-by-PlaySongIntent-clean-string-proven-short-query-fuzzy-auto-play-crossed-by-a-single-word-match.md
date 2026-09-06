---
id: JF-508
title: >-
  'la band' artist carrier absorbed by PlaySongIntent (clean-string proven) +
  short-query fuzzy auto-play crossed by a single-word match
status: To Do
assignee: []
created_date: '2026-09-06 15:25'
labels:
  - nlu
  - fuzzy-matching
  - routing
dependencies: []
references:
  - corr=269e622d
  - profile-nlu clean-string evidence
  - JF-379
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two model/routing findings from the 2026-09-06 Dot session. (A) The artist_carrier does NOT force the artist path: profile-nlu on the CLEAN strings 'suona la band soul coffee' AND 'suona la band soul coughing' both select PlaySongIntent with the whole phrase in the song slot and musician empty; PlaySong's greedy 'Suona {song}' family absorbs carrier+name wholesale (device-evidenced 17:21:39 corr=269e622d: heard 'soul coffee', routed to PlaySongIntent, song='soul coffee', auto-played the fuzzy match 'Starfish & Coffee' with the closest-match announce). The artist_carrier vocabulary exists in the it-IT template but the carrier samples are not winning; investigate the carrier sample shapes (they should be '{imperative} {artist_carrier} {musician}'-style with the catalog-backed musician slot) and strengthen them so a carrier phrase routes to PlayArtistSongsIntent. (B) The short-query fuzzy auto-play bar: a 2-word query where only ONE word matched exactly ('coffee' in 'Starfish & Coffee'; 'soul' matched nothing) crossed the auto-play threshold and played with the closest-match announce. Announced+cheap-to-stop is defensible UX, but a single-word match on a 2-word query auto-playing deserves a threshold check: log/inspect the score that crossed the bar (the announcement path is HandleFuzzyMiss's auto-accept at the user-threshold bar) and consider requiring higher coverage (or full keyword coverage) for queries of <=2 words. Also note the ASR chain that fed it: three hearings of 'coughing' across two devices (coughing/coffin/coffee) = the strongest evidence yet for JF-379's generative phonetics.
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
