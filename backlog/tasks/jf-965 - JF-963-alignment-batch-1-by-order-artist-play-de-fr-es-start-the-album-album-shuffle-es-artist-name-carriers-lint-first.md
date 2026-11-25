---
id: JF-965
title: >-
  JF-963 alignment batch 1: by-order artist play (de/fr/es), start-the-album,
  album-shuffle, es artist name-carriers (lint-first)
status: To Do
assignee: []
created_date: '2026-11-25 14:55'
updated_date: '2026-11-25 16:26'
labels:
  - nlu
  - alignment
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
JF-963 alignment batch 1: the maintainer-approved safe rows from the major-skill corpus (maintainer go 2026-11-25, 'si, tracciando tutto in un backlog prima'). PREMISE CORRECTED 2026-11-25 sera (Fable consult + template verification): the by-order artist-play family ALREADY EXISTS in all four locales — en-US on PlayArtistSongsIntent via the vocabulary product artist_media_verb (12) x artist_media_noun (songs/music/tracks/tunes) x artist_media_prep (by/from), de-DE explicit (Spiele/Hoere/Gib...wieder/Starte x Lieder/Musik/Songs von), es-ES explicit (Reproduce/Escucha/Pon/Toca/Suelta x canciones/musica de), fr-FR explicit (Lis/Ecoute/Mets/Lance x chansons/musica de). The old text's claim 'we only have the inverted order' (and 'en carries them on FindSong') was a research-mining error against the committed templates. THE REAL DELTA for item (1) is therefore only the MISSING VERBS/NOUNS vs the JF-963 corpus section 10, computed with the JF-970 `carriers` subcommand (candidate delta table; only ABSENT rows are work, per the nlu-debug-protocol Gate 1) — JF-970's AC #5 already folds this delta correction into that task; a locale whose delta is empty drops out of item (1). Items (2) 'start the album {album}' family (en + 4 major locales; Plex-documented) and (3) ALBUM SHUFFLE carrier ('shuffle the album {album}' per-locale) unchanged, same carriers-table check. (4) es artist name-carrier rows ('canciones del artista/cantante/grupo {musician}') — LINT-FIRST per Fable: check whether 'artista'/'cantante'/'grupo' collide with MediaInfoType synonyms (the band/singer #46 class) before adding. BASELINE MECHANISM (Fable correction, 2026-11-25 sera): it already EXISTS, do not build anything new — two `python3 scripts/nlu-lab/cli.py run --live` passes with the battery workingset immediately before the deploy, `compare --expect-zero-changes` between them for the A/A floor, then the same run post-deploy compared against the baseline; the catalog state at both moments is certified by JF-971's canary (until JF-971 lands, the manual Gate 2 canary applies); the informative erosion signal post-deploy is the empty-to-non-empty transition of consideredIntents on inverted-order and naked-artist forms; battery rows are the subsumption graph's NEW edges (graph diff-git + select) plus sentinel rows of intents sharing the carrier. The 9-state ablation is NOT the primary design (keep it for a row that goes red without explanation). DEPENDS ON: JF-970 (delta table), the capture part of JF-968 (full-hypothesis rows in runs), JF-971 (canary in run records), and JF-964 green for any en-US rows. EXECUTION DISCIPLINE per the adopted sequence, per row family: graph diff on a branch (stop on a new IDENTICAL edge; probe on a new SWALLOW edge with free/catalog slot) -> token lint + classify-fixtures on the branch -> lab round with the change-driven selection (card discipline per nlu-debug-protocol Gate 5) -> template edit + regenerate in ALL affected locales -> fixtures updated in the same change -> live verify after deploy. The probe-first list (noun-after playlist, open/go-to, give-me-a-suggestion, artist-radio, pon-a, par) is NOT in this batch: it stays in JF-963's notes pending its probes. The scartati (play some {genre} naked, Spiele das Lied, esegui) are never added.
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
