---
id: JF-865
title: >-
  Multi-medium disambiguation on greedy-handler misses: the JF-844 decision
  (PlaySong catching 'play nemo' offers the movie; carrier stays first-shot)
status: Done
assignee: []
created_date: '2026-10-11 20:20'
updated_date: '2026-10-11 22:14'
labels:
  - nlu
  - cross-media
  - feature
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-11 from the maintainer's JF-844 decision (chat): the bare-ask FIX is HANDLER-SIDE MULTI-MEDIUM, not model densification.

THE SHAPE (his words): on greedy-slot handlers whose own-medium search misses (the classic: PlaySongIntent catches a bare 'play nemo' because the NLU stole it there), search the OTHER media; if multiple high-confidence hits (or one confident cross-medium hit vs a weak own-medium miss), ask the disambiguation question; the carrier form ('riproduci il film X') remains the guaranteed first-shot path, NEVER disambiguated.

THE VEHICLE: the existing CrossMediaFallback architecture (TryEntityFallbackAsync, the JF-363 Confirm flow with crossmedia_notfound_* attrs, the JF-463 greedy-slot extension) extended from artist-only to VIDEO: a confirmed song-miss resolves against movies/episodes, gates on the CrossMedia thresholds + word-count guard family, and offers via the existing confirm/auto-serve/off modes. The 16-locale probe map (JF-864's notes) is the evidence base for which locales' steals become rescuable.

SCOPE CAVEAT (recorded honestly in JF-844): this rescues the STEAL locales (en-US/de/es-US/fr-CA: the request reaches PlaySong; en-AU/es-ES PlayByGenre and ja PlayArtistSongs may need their own fallback legs); it CANNOT help the NO_SELECTION locales (it-IT, en-GB, es-MX, fr-FR, pt-BR, hi) where the bare shape selects no intent and never reaches any handler - there the carrier stays the only path. That boundary goes in the FAQ/reference when this ships.

Verification: unit pins for the video fallback leg (miss -> offer/confirm/auto-serve per mode), the word-count guard, the carrier-never-disambiguated invariant; a live probe wave on the steal locales post-deploy.
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
