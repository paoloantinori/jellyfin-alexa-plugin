---
id: JF-917
title: >-
  en-US: "put on music by X" steals to PlaySongIntent with a phantom song fill
  (sample competition with "put on {song} by {musician}"; the artist row exists)
status: To Do
assignee: []
created_date: '2026-10-20 10:55'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Residual from the JF-914 en/de/fr audit (2026-10-20, probe-proven stable 2/2): the en-US one-shot "ask jellyfin player to put on music by pink floyd" routes PlaySongIntent with a PHANTOM song fill ("\"Put Some Music On\" by Boyz II Men"; the bare payload steals too, different phantom). NOT a wrapper-tail gap: the exact PlayArtistSongs row "put on music by {musician}" exists in templates/en-US.yaml (the {artist_media_verb} x {artist_media_noun} x {artist_media_prep} product); the steal is PlaySong's competing committed exact-pattern sample "put on {song} by {musician}" absorbing song="music" (the documented phantom-MusicRecording class, the JF-399 template comment). The it-IT regression class does NOT apply here (en's wrapper payload is the bare imperative and it routes; "play music by X" and "listen to music by X" are GREEN full-pipeline). FIX SHAPE: a sample-competition problem between two committed en samples; options to weigh probe-first: (a) qualify the PlaySong sample (e.g. "put on the song {song} by {musician}") - a committed-sample EDIT, the JF-541 statistics discipline applies and device verification is mandatory (the sample may be load-bearing for real song asks "put on X by Y"); (b) add a higher-signal PlayArtistSongs anchor. Whatever lands needs the en battery + fixtures + a model deploy. Verification: "ask jellyfin player to put on music by pink floyd" routes PlayArtistSongsIntent on simulate AND "put on X by Y" song asks still route PlaySong (the reverse guard must be probed BEFORE choosing a shape).
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
