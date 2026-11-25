---
id: JF-965
title: >-
  JF-963 alignment batch 1: by-order artist play (de/fr/es), start-the-album,
  album-shuffle, es artist name-carriers (lint-first)
status: To Do
assignee: []
created_date: '2026-11-25 14:55'
labels:
  - nlu
  - alignment
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
JF-963 alignment batch 1: the maintainer-approved safe rows from the major-skill corpus (maintainer go 2026-11-25, 'si, tracciando tutto in un backlog prima'). Scope, per the Fable-reviewed recommendations: (1) BY-ORDER ARTIST PLAY rows in de/fr/es (en already carries them on FindSong; the Amazon/Plex/Emby/MyMedia-consensus order 'play songs by {artist}' / 'play music by {artist}' and their per-locale equivalents: es 'reproduce canciones de' family exists — add the missing by-order equivalents, fr 'jouer des chansons de / passer les chansons de', de 'spiele lieder von / musik von {musician} ab'); each locale's exact wording cross-checked against the corpus report section 10 and the locale's own carrier conventions. (2) 'start the album {album}' family (en + the 4 major locales; Plex-documented 'Start the record Illuminate' class). (3) ALBUM SHUFFLE carrier ('shuffle the album {album}' / per-locale; Plex-documented). (4) es artist name-carrier rows ('canciones del artista/cantante/grupo {musician}') — LINT-FIRST per Fable: check whether 'artista'/'cantante'/'grupo' collide with MediaInfoType synonyms (the band/singer #46 class) before adding. EXECUTION DISCIPLINE per the adopted sequence, per row family: graph diff on a branch (stop on a new IDENTICAL edge; probe on a new SWALLOW edge with free/catalog slot) -> token lint + classify-fixtures on the branch -> lab round with the change-driven selection -> template edit + regenerate in ALL affected locales -> fixtures updated in the same change -> live verify after deploy. en-US rows in this batch ONLY after JF-964 comes back green. The probe-first list (noun-after playlist, open/go-to, give-me-a-suggestion, artist-radio, pon-a, par) is NOT in this batch: it stays in JF-963's notes pending its probes. The scartati (play some {genre} naked, Spiele das Lied, esegui) are never added.
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
