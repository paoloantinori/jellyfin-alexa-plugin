---
id: JF-422
title: >-
  Empty-album elicit reads album titles as artist names and dead-ends the
  musician flow (JF-411 path unreachable from elicit)
status: To Do
assignee: []
created_date: '2026-08-31 15:02'
labels:
  - code-review
  - dialog
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs:134
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs:176
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review finding (2026-08-31, high effort, CONFIRMED by code reading). PlayAlbumIntentHandler.cs:134 (empty-album elicit branch), interacts with the JF-411 block at line 176.

DEFECT (two failure directions):
1. 'riproduci un album' (both slots empty) elicits the MUSICIAN slot; the user answers with the album title they wanted ('the dark side of the moon'), it is captured as an artist, ArtistSearch finds nothing, and they get terminal NotFoundAlbumByArtist for an album that exists.
2. For the motivating JF-411 case (musician present, album empty, e.g. 'un disco dei' after ASR swallowed the name), the musician answer returns with dialogState IN_PROGRESS and line 134 returns an album-title elicit BEFORE the JF-411 block at 176 can run: a user who wanted 'any album by Koop' is asked a question they cannot answer. The JF-411 play-without-a-title resolution the comment promises is unreachable from the elicit path.

FIX SHAPE: elicit the ALBUM slot first (title answers are the common case), and route the IN_PROGRESS musician answer into the album-by-artist resolution (the JF-411 block) instead of re-elicitating the title. Consider slot-presence-driven branching: album empty + musician filled = JF-411 path; both empty = elicit album first.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 'riproduci un album' (empty slots) elicits slots in an order where an album-title answer leads to an album search, not an artist search misread
- [ ] #2 The JF-411 motivating case works from the elicit path: musician answer during dialogState IN_PROGRESS reaches the album-by-artist resolution (play any album by that artist), not an album-title prompt
- [ ] #3 Unit tests: (a) empty-album elicit + title answer resolves the album; (b) musician elicit answer plays an album by that artist
- [ ] #4 No regression on the direct 'un disco dei X' one-shot forms (JF-411 originals)
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
