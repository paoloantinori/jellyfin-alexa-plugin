---
id: JF-132
title: >-
  Fix NLU ambiguity: en-US "play bohemian rhapsody by queen" resolves to
  PlayArtistSongsIntent instead of PlaySongIntent
status: Done
assignee: []
created_date: '2026-05-12 09:41'
updated_date: '2026-05-12 11:33'
labels:
  - nlu
  - en-US
  - bug
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The utterance "play bohemian rhapsody by queen" (en-US) resolves to PlayArtistSongsIntent instead of PlaySongIntent. The "by queen" phrase triggers artist matching, overriding the song intent.

The interaction model needs better disambiguation between PlaySongIntent and PlayArtistSongsIntent when both a song name and artist are present. Consider adding more concrete sample utterances with "song" keyword for PlaySongIntent.

NLU test fixture: en-US - "play bohemian rhapsody by queen" (expected PlaySongIntent, got PlayArtistSongsIntent)
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
- [ ] #2 dotnet build passes with 0 errors
- [ ] #3 dotnet test passes
- [ ] #4 No new compiler warnings introduced
- [ ] #5 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #6 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #7 NLU test fixtures updated if interaction model changed
- [ ] #8 E2E test added for new intent or handler logic
- [ ] #9 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed by adding 15 new sample utterances to PlaySongIntent with explicit "song" keyword + "by {musician}" anchoring patterns. This gives Alexa's NLU stronger disambiguation signal when both song name and artist are present. All 1019 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
