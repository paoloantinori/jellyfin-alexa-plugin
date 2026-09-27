---
id: JF-642
title: >-
  JF-642 - ja-JP noun-qualified artist carriers stolen by PlayByGenre (the
  free-text genre capture): even canonical pre-existing forms route to genre,
  artist intent unreachable by noun voice
status: To Do
assignee: []
created_date: '2026-09-27 06:19'
labels:
  - nlu
  - ja-JP
  - device-found
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 from the JF-399 residue-pass live verification (same-turn rule).

LIVE EVIDENCE (profile-nlu on the live rebuilt model, two artists probed): ja-JP noun-qualified artist carriers are broadly stolen by PlayByGenreIntent - 'クイーン の曲を再生して' -> PlayByGenre{genre:'クイーン'}, 'ビートルズ の曲を再生して' -> PlayByGenre, 'ビートルズ の音楽を聴かせて' -> PlayByGenre. Even the CANONICAL pre-existing carrier ('{musician} の曲を再生して', present since before this pass) is stolen. The new carriers from the residue pass (を聴かせて/をスタートして noun forms) land on PlayByGenre or PlaySong{song:'クイーン'} - NOT PlayArtistSongsIntent. Bare '{musician} を聞きたい' -> PlaySong{song}. The worker's header documented this JF-406-class steal risk; the probe confirms it is the dominant behavior, not an edge.

SCOPE: (1) instrument: which PlayByGenre sample is winning (the genre slot is free-text SearchQuery or custom Genre type? check the ja template - if Genre is a custom type, artist names should not resolve as genre values; if it captures, the FIX is genre-type hygiene or carrier re-qualification); (2) the ja PlayArtistSongs carrier space needs the genre-steal resolved before any artist enrichment can be verified - the live NLU wrapper battery fixtures for ja (intentionally untouched by the residue pass) are the probe; (3) check whether the same steal affects ja PlaySong noun forms ('の歌 を...' shapes) and the ja PlayBook JF-406 note - one root cause (the free-text genre slot) may explain multiple documented ja steals.

PRIORITY: medium. This is the known JF-406 divergence class concentrated in ja-JP; the residue pass's ja samples are additive and harmless (they route SOMETHING), but the artist intent is effectively unreachable by noun-qualified voice in ja today.
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
