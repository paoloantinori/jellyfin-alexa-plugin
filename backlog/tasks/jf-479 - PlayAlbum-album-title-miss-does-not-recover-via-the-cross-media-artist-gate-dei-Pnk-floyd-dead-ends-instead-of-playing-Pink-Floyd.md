---
id: JF-479
title: >-
  PlayAlbum album-title miss does not recover via the cross-media artist gate
  ('dei P!nk floyd' dead-ends instead of playing Pink Floyd)
status: To Do
assignee: []
created_date: '2026-09-04 09:58'
labels: []
dependencies: []
references:
  - Device corr=f74eb567 (2026-09-04)
  - JF-446 (the shared cross-media gate)
  - JF-381 (P!nk-class phonetic flagship)
  - JF-469 (ASR slot bleed family)
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-04 batch re-test (corr=f74eb567): 'riproduci l'album dei pink floyd' arrived as PlayAlbumIntent with album='dei P!nk floyd' (the ASR put the clitic AND the artist name entirely in the album slot; musician empty). The album-title path correctly not-found that literal, but the response was a dead-end 'Spiacente, non ho trovato nessun album chiamato dei P!nk floyd' — no cross-media artist recovery, even though the raw slot is a near-perfect artist query once the Italian stop-word 'dei' is stripped ('P!nk floyd' -> Pink Floyd phonetically, the flagship JF-381 class, and exactly 2 content words inside the CrossMediaArtistMaxWords guard).

INVESTIGATE (either explanation changes the fix): (a) PlayAlbum's album-title miss path may not wire TryEntityFallbackAsync at all (the JF-446 shared gate is wired on PlaySong/PlayMoodMusic/FindSong/PlayByGenre/PlayAlbum-musician paths; check the ALBUM-TITLE miss path specifically); or (b) it is wired but a gate rejected: the word-count guard would pass (2 words), so check what else could reject — the threshold with the raw vs tokenized query, or the JF-471 acceptance gate if the artist chain ran (it should NOT: that gate is musician-path only).

Also worth noting for the model layer: the it-IT sample 'riproduci l'album dei {musician}' exists; the ASR/NLU put the whole span in album anyway (statistical filler again). The handler-side recovery is the right layer per the JF-469 evidence.

Fix: wire/reach the shared cross-media artist gate on PlayAlbum's album-title miss (same call shape as the musician path), so 'dei P!nk floyd' plays Pink Floyd with the FoundArtistInstead announcement. Respect AnnounceCrossMediaSubstitution. Unit tests: clitic-bleed album slot -> artist plays with announcement; a genuine album-shaped miss (e.g. 'magical mystery tour' when absent) still clean-not-founds (word-count/gate discipline unchanged).
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
