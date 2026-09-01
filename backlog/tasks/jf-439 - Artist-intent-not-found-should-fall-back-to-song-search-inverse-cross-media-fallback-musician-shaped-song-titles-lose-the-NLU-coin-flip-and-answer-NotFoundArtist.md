---
id: JF-439
title: >-
  Artist-intent not-found should fall back to song search (inverse cross-media
  fallback): musician-shaped song titles lose the NLU coin flip and answer
  NotFoundArtist
status: To Do
assignee: []
created_date: '2026-09-01 14:45'
labels:
  - nlu
  - artist-search
  - ux
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs:425
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - 'tests/integration/fixtures/e2e_it-IT.yaml:128'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up from JF-438 (2026-09-01): removing the RepeatSingle "Suonala ancora" collision samples fixed the two E2E NLU regressions but exposed a coin flip that was previously masked: "suona/metti la canzone X" where X is a MUSICIAN-SHAPED song title (e.g. Soul Coughing's "Sugar Free Jazz") now routes to PlayArtistSongsIntent with musician="sugar free jazz" (the NLU drops the noun and feeds the tail to the Musician slot), and the handler answers NotFoundArtist because no artist has that name. Song-shaped titles (bohemian rhapsody, screenwriter's blues, yesterday) still route to PlaySongIntent correctly, and the "il brano" carrier variant survives even for musician-shaped titles.

This is the INVERSE of the existing Cross-Media-Type Fallback (BaseHandler.TryEntityFallbackAsync handles song-slots-that-are-artists; nothing handles artist-slots-that-are-songs). The NLU coin flip in the sample-identical region ("Suona la canzone {song}" vs "Suona la {musician}") cannot be won model-side; handler-side tolerance is the durable fix, per the codebase's established pattern.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 PlayArtistSongsIntentHandler not-found path (currently a bare NotFoundArtist Tell at ~line 428): before giving up, search songs by the musician value (n-gram + phonetic, the FindSong/PlaySong stage 1-2 machinery) and play the best match with a FoundSongInstead-style announcement (mirror of FoundArtistInstead)
- [ ] #2 Word-count guard like CrossMediaArtistMaxWords: only fall back for multi-word values (a single word is a poor song query)
- [ ] #3 Announcement locale key added to all 17 locales if no suitable key exists (check: FoundSongInstead may not exist; FoundArtistInstead does)
- [ ] #4 Unit tests: no artist + matching song -> plays with announcement; no artist + no song -> clean NotFoundArtist unchanged
- [ ] #5 E2E/NLU guard for the motivating case once stable: 'suona la canzone sugar free jazz' serves the song regardless of which intent wins the NLU coin flip
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining)
- [ ] #11 Findings applied or tracked
<!-- DOD:END -->
