---
id: JF-42
title: Recommendation intent
status: Done
assignee: []
created_date: '2026-05-03 13:37'
updated_date: '2026-05-03 15:04'
labels:
  - enhancement
  - intent
  - voice-interaction
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add Alexa intent to recommend media based on the user's watch/listen history. Inspired by Kodi and Spotify.

Support utterances like:
- "Recommend something to watch"
- "Recommend some music"
- "Suggest a movie"
- "Play something I'd like"

Implementation: Query Jellyfin API for the user's most-played or highest-rated items, then use genre/artist similarity to suggest new content. Can leverage Jellyfin's built-in suggestion API (Items/Recommendations) or implement a simple collaborative approach based on the user's own listening history.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented RecommendIntentHandler with genre-based recommendations from user play history. Extracts top genres from played items, queries unplayed items in those genres, falls back to recent unplayed items. Supports optional media_type filter. 6 unit tests passing.
<!-- SECTION:FINAL_SUMMARY:END -->
