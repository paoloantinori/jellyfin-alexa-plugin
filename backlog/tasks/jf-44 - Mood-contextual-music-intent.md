---
id: JF-44
title: Mood/contextual music intent
status: Done
assignee: []
created_date: '2026-05-03 13:37'
updated_date: '2026-05-03 15:14'
labels:
  - enhancement
  - intent
  - voice-interaction
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add Alexa intent to play music matching a mood or context. Inspired by Spotify and the AudioMuse Jellyfin plugin.

Support utterances like:
- "Play something relaxing"
- "Play upbeat music"
- "Play chill music"
- "Play something energetic"
- "Play focus music"

Implementation: Map mood keywords to genre/tag filters in Jellyfin. If the library has mood tags (from MusicBrainz or manual tagging), use those directly. Fallback: map common mood words to likely genres (relaxing → ambient/acoustic/jazz, upbeat → pop/rock/dance). Consider integrating with the AudioMuse plugin if installed.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented PlayMoodMusicIntentHandler with mood-to-genre mapping for 10 moods. Falls back to using mood as raw genre query. Added locale strings and interaction models for 12 locales. 6 unit tests passing.
<!-- SECTION:FINAL_SUMMARY:END -->
