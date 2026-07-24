---
id: JF-264
title: FindSongIntent should announce matched song name before playing
status: Done
assignee: []
created_date: '2026-06-06 13:38'
updated_date: '2026-06-06 15:19'
labels:
  - ux
  - handler
  - findsong
  - enhancement
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When using FindSongIntent ("sto cercando una canzone"), the user provides a song name, the handler finds a match and immediately starts playing it. The Echo Show displays the title visually, but Alexa does NOT speak the song name aloud before playing.

This is a UX problem: the user needs to hear what song was matched to confirm it's the right one, especially when the match is fuzzy (e.g., "role of my family" might match "Role of My Family" or something similar).

**Expected behavior:** Before starting playback, the skill should speak the song name, e.g., "Sto suonando Role of My Family" or "Trovato: Role of My Family" and then play.

**Current behavior:** Silent playback start with only visual title on Echo Show.

**Affected handler:** FindSongIntentHandler (or whatever handler resolves the FindSong flow)

**Reproduction:**
1. "Alexa, chiedi a mia collezione sto cercando una canzone"
2. When prompted, say "role of my family"
3. Song plays immediately without spoken confirmation

**Note:** This likely also affects other search-to-play flows where fuzzy matching picks a result. Check PlaySongIntentHandler and SearchMediaIntentHandler for the same pattern.
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Already implemented: FindSongIntentHandler.SearchAndRespondAsync (line 443-445) and HandleDisambiguatingAsync (line 351-353) both set OutputSpeech with FindSongFoundOne response string ('Ho trovato {0} di {1}. La riproduco.') in all 17 locales. If the user is still not hearing the announcement, it may be a device-specific issue (some Echo devices suppress OutputSpeech when AudioPlayer.Play starts quickly). PlaySongIntentHandler does NOT announce — that would be a separate task.
<!-- SECTION:NOTES:END -->
