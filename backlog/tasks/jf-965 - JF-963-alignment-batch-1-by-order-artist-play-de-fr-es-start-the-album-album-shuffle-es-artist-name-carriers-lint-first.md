---
id: JF-965
title: >-
  JF-963 alignment batch 1: by-order artist play (de/fr/es), start-the-album,
  album-shuffle, es artist name-carriers (lint-first)
status: To Do
assignee: []
created_date: '2026-11-25 14:55'
updated_date: '2026-11-25 16:19'
labels:
  - nlu
  - alignment
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
JF-963 alignment batch 1: the maintainer-approved safe rows from the major-skill corpus (maintainer go 2026-11-25, 'si, tracciando tutto in un backlog prima'). PREMISE CORRECTED 2026-11-25 sera (Fable consult + template verification): the by-order artist-play family ALREADY EXISTS in all four locales — en-US on PlayArtistSongsIntent via the vocabulary product artist_media_verb (12) x artist_media_noun (songs/music/tracks/tunes) x artist_media_prep (by/from), de-DE explicit (Spiele/Hoere/Gib...wieder/Starte x Lieder/Musik/Songs von), es-ES explicit (Reproduce/Escucha/Pon/Toca/Suelta x canciones/musica de), fr-FR explicit (Lis/Ecoute/Mets/Lance x chansons/musique de). The old text's claim 'we only have the inverted order' (and 'en carries them on FindSong') was a research-mining error against the committed templates. THE REAL DELTA for item (1) is therefore only the MISSING VERBS/NOUNS vs the JF-963 corpus section 10 (e.g. fr Jouer/Passer forms, de abspielen/ab-tail forms, corpus verbs per locale): compute the exact delta with the matcher BEFORE writing any battery row; if a locale's delta turns out empty, that locale drops out of item (1). Items (2) 'start the album {album}' family (en + 4 major locales; Plex-documented) and (3) ALBUM SHUFFLE carrier ('shuffle the album {album}' per-locale) unchanged. (4) es artist name-carrier rows ('canciones del artista/cantante/grupo {musician}') — LINT-FIRST per Fable: check whether 'artista'/'cantante'/'grupo' collide with MediaInfoType synonyms (the band/singer #46 class) before adding. BASELINE PROTOCOL (Fable-consulted, 2026-11-25): before deploying the batch, take the consideredIntents baseline IMMEDIATELY before the deploy (catalog state must match between baseline and post; the artist catalog was mid-propagation during the consult) as a DOUBLE baseline (two passes on the current model) to measure the considered list's own variance; the informative erosion signal post-deploy is the empty-to-non-empty transition of consideredIntents on inverted-order and naked-artist forms (greens usually carry an empty list); the battery rows are the subsumption graph's NEW edges plus sentinel rows of intents sharing the carrier. The 9-state ablation is NOT the primary design here (it answers which wiring causes a red, keep it for a row that goes red without explanation). DEPENDS ON the capture part of JF-968 (parser + ledger considered-hypothesis row) landing FIRST so the baseline can be recorded. EXECUTION DISCIPLINE per the adopted sequence, per row family: graph diff on a branch (stop on a new IDENTICAL edge; probe on a new SWALLOW edge with free/catalog slot) -> token lint + classify-fixtures on the branch -> lab round with the change-driven selection -> template edit + regenerate in ALL affected locales -> fixtures updated in the same change -> live verify after deploy. en-US rows in this batch ONLY after JF-964 comes back green. The probe-first list (noun-after playlist, open/go-to, give-me-a-suggestion, artist-radio, pon-a, par) is NOT in this batch: it stays in JF-963's notes pending its probes. The scartati (play some {genre} naked, Spiele das Lied, esegui) are never added.
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
