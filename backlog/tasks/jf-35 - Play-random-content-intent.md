---
id: JF-35
title: Play random content intent
status: Done
assignee: []
created_date: '2026-05-03 13:36'
updated_date: '2026-05-03 14:02'
labels:
  - enhancement
  - intent
  - voice-interaction
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add new Alexa intent to play random content from the library. Inspired by Kodi/Kanzi and Navidrome skills.

Support utterances like:
- "Play a random movie"
- "Play a random album"
- "Play a random song"
- "Play a random song from {genre}"

Implementation: Use Jellyfin API's random/SortBy.Random parameter to fetch random items from the library. Support filtering by media type (audio, video) and optionally by genre.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented PlayRandomIntent with media_type and genre slot support. Handler fetches items with bounded query (500 limit), shuffles via Fisher-Yates with thread-safe Random.Shared, expands albums to tracks, and supports both audio and video playback. Registered in controller, added to all 12 interaction models, localized strings in all 12 locale files, 9 unit tests passing.
<!-- SECTION:FINAL_SUMMARY:END -->
