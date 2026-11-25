---
id: JF-965
title: >-
  JF-963 alignment batch 1: by-order artist play (de/fr/es), start-the-album,
  album-shuffle, es artist name-carriers (lint-first)
status: To Do
assignee: []
created_date: '2026-11-25 14:55'
updated_date: '2026-11-25 17:06'
labels:
  - nlu
  - alignment
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
JF-963 alignment batch 1: the maintainer-approved safe rows from the major-skill corpus (maintainer go 2026-11-25, 'si, tracciando tutto in un backlog prima'). REAL DELTA COMPUTED WITH THE CARRIERS TOOL (JF-970 AC #5, 2026-11-25, /tmp/jf965_candidates.tsv, 17 candidates run; zero SAME_CARRIER hits anywhere): ABSENT rows are the only work — (1) by-order artist: fr-FR 'Joue des chansons de {musician}' + 'Joue de la musique de {musician}' (the F3 indefinite-plural Joue family; ours is definite-only Lis les chansons de), de-DE 'Spiele Lieder von dem Künstler {musician}' + 'Spiele Musik von der Band {musician}' + 'Spiele Musik von der Gruppe {musician}' (the D3 carrier-noun rows); en-US DROPS OUT of item (1): 'play songs by {musician}' and 'play music by {musician}' are PRESENT (the vocabulary product), killing the A1 'we hold neither order' premise. (2) start-the-album: en-US 'start the album {album}' + 'start the record {album}' (A4), de-DE 'Starte das Album {album}'; fr-FR DROPS OUT: 'Lance l'album {album}' is PRESENT. (3) album-shuffle: en-US 'shuffle the album {album}' ONLY (the A2 artist-shuffle half 'shuffle music/songs by {musician}' is PRESENT via the product; the research missed that artist_media_verb includes 'shuffle'); per-locale album-shuffle wording is not corpus-pinned and stays out unless derived at execution from the locale's carrier conventions with the same carriers check. (4) es name-carriers, LINT-FIRST unchanged: 'reproduce canciones del artista/cantante/grupo {musician}' all three ABSENT; check the MediaInfoType synonym collision (the band/singer #46 class) BEFORE adding. TWELVE ABSENT rows total (fr 2, de 4 including Starte das Album, en 3, es 3; five candidates read PRESENT and drop out). BASELINE MECHANISM (unchanged, exists): two `python3 scripts/nlu-lab/cli.py run --live` passes with the battery workingset immediately before the deploy, `compare --expect-zero-changes` for the A/A floor, then post-deploy compare; catalog state certified by JF-971's canary when it lands (manual Gate 2 canary until then); the erosion signal is consideredIntents empty-to-non-empty on inverted-order and naked-artist forms; battery rows are the subsumption graph's NEW edges (graph diff-git + select) plus carrier-sharing sentinels. The 9-state ablation is NOT the primary design (keep it for unexplained reds). DEPENDS ON: JF-970 (landed: the carriers table above IS its AC #5), the capture part of JF-968 (landed: full-hypothesis rows), JF-971 (canary in run records), and JF-964 green for any en-US rows. EXECUTION DISCIPLINE per the adopted sequence, per row family: graph diff on a branch (stop on a new IDENTICAL edge; probe on a new SWALLOW edge with free/catalog slot) -> token lint + classify-fixtures on the branch -> lab round with the change-driven selection (card discipline per nlu-debug-protocol Gate 5) -> template edit + regenerate in ALL affected locales -> fixtures updated in the same change -> live verify after deploy. The probe-first list (noun-after playlist, open/go-to, give-me-a-suggestion, artist-radio, pon-a, par) is NOT in this batch: it stays in JF-963's notes pending its probes. The scartati (play some {genre} naked, Spiele das Lied, esegui) are never added.
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
