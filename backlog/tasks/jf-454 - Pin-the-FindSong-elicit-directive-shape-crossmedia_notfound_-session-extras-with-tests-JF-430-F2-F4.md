---
id: JF-454
title: >-
  Pin the FindSong elicit directive shape + crossmedia_notfound_* session extras
  with tests (JF-430 F2/F4)
status: To Do
assignee: []
created_date: '2026-09-02 04:00'
labels:
  - test-coverage
  - dialog
dependencies: []
references:
  - Alexa/Handler/Intent/FindSongIntentHandler.cs
  - Alexa/Handler/BaseHandler.cs
  - Alexa/Handler/Intent/NoIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-430 simplify round (2026-09-02, findings F2/F4, flagged as coverage gaps on the new shared paths). F2: no test pins the FindSong elicit directive shape - PlaySong and PlayAlbum elicits are pinned (EmptyMusicianSlotTests:185, PlayAlbumIntentHandlerTests:524) but FindSong's wrapper (the one carrying FindSongSessionData attrs + FindSongKeys activation, FindSongIntentHandler.cs:913) has no directive-shape assertion (slotToElicit, updatedIntent slot list, session attributes present). F4: no test asserts the crossmedia_notfound_* extras land in session attributes - CrossMediaTypeFallbackTests:767 checks disambig_type only, so the BuildAttributes extraEntries path (the exact consolidation JF-430 made) is unverified; a reader change to NoIntentHandler:92 would silently break the "No returns the clean song/album not-found" behavior. Two small test additions; no production change.
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
