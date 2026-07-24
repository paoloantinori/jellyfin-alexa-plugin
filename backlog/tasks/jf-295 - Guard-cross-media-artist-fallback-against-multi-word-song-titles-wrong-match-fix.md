---
id: JF-295
title: >-
  Guard cross-media artist fallback against multi-word song titles (wrong match
  fix)
status: Done
assignee: []
created_date: '2026-06-16 08:33'
updated_date: '2026-06-16 09:55'
labels:
  - bug
  - nlu
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlaySongIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/FuzzyMatcher.cs
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlaySongIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When a song is not found and there is NO musician slot, PlaySongIntentHandler's cross-media fallback searches the SONG TITLE as an artist query, causing wrong matches. Verified: Alexa/Handler/Intent/PlaySongIntentHandler.cs:208-237 calls ArtistSearch.SearchAsync(songQuery, ...) at line 215, then FuzzyMatcher.FindBestMatchWithScore at line 222 against the default threshold (FuzzyMatcher.DefaultThreshold=60). Observed in 2026-06-16 logs: 'la ballata del genesio' fuzzy-matched artist 'Lamb' with score 75 (>= 60), so the user got the wrong artist. Log line: "PlaySong: artist fallback found 'Lamb' with score=75 for query='la ballata del genesio'".

The fallback's stated intent (comment at line 210-212) is to catch NLU misroutes of SHORT artist names into the song slot (e.g. 'strokes' → 'The Strokes'). A multi-word song title is a poor artist query and should not trigger it.

Goal: only attempt the artist fallback for plausibly-short queries (e.g. <=2 words) and/or require a higher fuzzy threshold (~85) for cross-media matches, so genuine song-title misses return a clean 'song not found' instead of playing an unrelated artist. Keep the PlaySong-with-musician-slot path unchanged.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A multi-word song title that misses (e.g. 'la ballata del genesio') returns the 'song not found' response instead of fuzzy-matching an unrelated artist
- [ ] #2 Short artist-name misroutes (e.g. 'strokes' → 'The Strokes') still resolve via the cross-media fallback
- [ ] #3 The PlaySong-with-musician-slot path is unchanged (no regression)
- [ ] #4 Existing PlaySong fallback unit tests updated; new tests cover the word-count guard and the higher cross-media threshold
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added a word-count gate (<=2 words) and a higher cross-media threshold (85 vs default 60) to PlaySongIntentHandler's cross-media artist fallback. Multi-word song-title misses now return a clean 'song not found' instead of fuzzy-matching an unrelated artist (fixes 'la ballata del genesio' -> 'Lamb', score 75). Short misroutes ('strokes' -> 'The Strokes') still resolve; the PlaySong-with-musician-slot path is unchanged. 3 new tests in CrossMediaTypeFallbackTests (multi-word skips + no artist search issued; short misroute resolves; musician-slot unaffected). Build green (0 warnings), 2403 tests pass. Committed 9d36093.
<!-- SECTION:FINAL_SUMMARY:END -->

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
