---
id: JF-478
title: >-
  PlayAlbum title path auto-plays interior-containment album matches (dark side
  of the moon -> 'O' by Damien Rice); JF-408 gate missing on this path
status: Done
assignee: []
created_date: '2026-09-04 09:57'
updated_date: '2026-09-04 10:55'
labels: []
dependencies: []
references:
  - Device corr=80bb4642 (2026-09-04)
  - 'JF-408 (interior containment, cascade-side)'
  - JF-471 (the musician-path gate; different path)
  - PlaySongAlbumFallbackTests.PlaySong_InteriorContainmentAlbum_NotSubstituted
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-04 batch re-test (corr=80bb4642): 'riproduci album dark side of the moon' arrived with the album slot FILLED (real ASR fills it, unlike profile-nlu where the musician slot steals it: the two probe surfaces genuinely differ). PlayAlbum's album-title search missed, the fuzzy fallback matched Damien Rice's single-letter album 'O' at containment score 90, and the skill auto-played it: 'Ho trovato l'album O' + Damien Rice tracks.

This is EXACTLY the JF-408 interior-containment class (a coincidental substring containment reaching the 90 bar must not substitute): that rejection exists in TryAlbumFallbackAsync (the PlaySong cascade, pinned by PlaySong_InteriorContainmentAlbum_NotSubstituted) but NOT on PlayAlbum's primary album-title path (the HandleFuzzyMiss auto-play at >= 90 or the equivalent acceptance point). The JF-471 gate protects only the album-by-artist (musician) path.

Fix: extend the JF-408 interior-containment rejection (shared helper IsInteriorContainment in BaseHandler, reused by the cascade) to PlayAlbum's album-title acceptance point. Mirror the existing cascade test at the PlayAlbum level: album 'O' in the library + query 'dark side of the moon' -> clean NotFoundAlbumByName (or the CrossMediaArtistSuggestion offer per JF-363 if the artist band applies), never auto-play. The legit single-word containment classes the JF-408 research established must keep working (verify against the cascade's test set).
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented, deployed, and live-verified by corr replay (commit a5d446e9, with JF-479 in the same commit: the two fixes share the predicate/guard machinery).

Root cause (device corr=80bb4642, pinned red-first): the JF-408 rejection WAS wired on PlayAlbum's title path, but its predicate demanded strictly-INTERIOR occurrences; the winning 'o' in 'of' is word-initial, so the rule never fired and 'O' auto-played at containment 90. Fix: the predicate (renamed IsEmbeddedContainment per the review) rejects every embedded occurrence (interior + word-initial + word-final fragments) with an affix tolerance (overflow <= 2, candidates >= 3) preserving the whole-word ('u2') and plausible-affix ('outkasts') classes; the one widening production caller (PlayArtistSongs tier-4) lands on the yes/no prompt, the JF-377 safe direction. Blast radius enumerated by the review: all three decision points (PlayArtistSongs tier-4, PlayAlbum fuzzy, the PlaySong cascade) verified in the intended direction, JF-377/JF-420 pins green.

Live verification on minix post-deploy: simulator replay of the exact device slot (album='dark side of the moon') returns 'Spiacente, non ho trovato nessun album chiamato dark side of the moon' with no playback: the incident closed. Suite 3128/3128, mutation surgical (predicate constant neutralized: exactly the 4 gate tests red, mirrors green). Device re-verification item for Paolo folded into the final card.
<!-- SECTION:FINAL_SUMMARY:END -->
