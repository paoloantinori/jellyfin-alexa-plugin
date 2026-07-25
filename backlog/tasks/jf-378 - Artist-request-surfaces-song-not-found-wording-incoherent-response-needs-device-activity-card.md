---
id: JF-378
title: >-
  Artist request surfaces song not-found wording (incoherent response) - needs
  device activity card
status: To Do
assignee: []
created_date: '2026-07-25 17:57'
labels:
  - ux
  - nlu
  - artist-search
  - wording
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
User reported (2026-07-25): asked Alexa to play the band 'Koop', got a not-found response about a SONG (incoherent with the artist request). Investigation showed: the PlaySong not-found wording ('Spiacente, non ho trovato nessuna canzone...') is correct for a song request, and PlayArtist finds Koop correctly in the simulator. So the incoherent wording was NOT reproduced at the handler level.

Most likely cause (unconfirmed): on the device, ASR transcribed 'Koop' (a foreign word on an it-IT Echo) into something that routed to PlaySongIntent instead of PlayArtistSongsIntent, so the user heard a song not-found for an artist request. This is the profile-nlu-vs-on-device divergence in the ASR direction. BLOCKED on the user providing the Alexa activity card showing the 'heard' text + chosen intent for the failed request.

Secondary angle: even if routing is correct, the cross-media fallback (TryEntityFallbackAsync, CLAUDE.md) can surface song/album wording from an artist-adjacent request. Worth making the not-found wording context-aware regardless of the Koop root cause.

Distinct from the ASR transcription issue itself (which may need the Romance Phonetic Synonyms machinery applied to artist names, or a catalog-sync of the user's artists).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Map every path where an artist request can surface song/album not-found wording (cross-media fallback, intent misroute, ASR garbling the slot so a different intent fires)
- [ ] #2 Decide: make the not-found wording reflect the original request type (artist -> artist not-found wording), OR accept the cross-media wording but make it explicit ('I couldn't find that artist; did you mean the song X?')
- [ ] #3 If wording change: update locale strings across 17 locales consistently
- [ ] #4 Needs the user's Alexa activity card for the original Koop failure to confirm WHICH path produced the incoherent wording before designing the fix
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
