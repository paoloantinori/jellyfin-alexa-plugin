---
id: JF-36
title: Play by genre intent
status: Done
assignee: []
created_date: '2026-05-03 13:36'
updated_date: '2026-05-03 14:10'
labels:
  - enhancement
  - intent
  - voice-interaction
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add new Alexa intent to play media filtered by genre. Inspired by Kodi and Navidrome skills.

Support utterances like:
- "Play some jazz music"
- "Play rock songs"
- "Play electronic music"
- "Play {genre} music"

Implementation: Use Jellyfin API's genre filtering to fetch and play items matching a specific genre. Requires adding a GENRE slot type to the interaction model with common genre values.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented PlayByGenreIntent with genre slot support. Handler queries Audio items filtered by genre with bounded query (500 limit), builds playback queue, and returns AudioPlayer response. Handles missing genre slot and no-results cases. Registered in controller, added to all 12 interaction models with locale-appropriate utterances, localized strings in all 12 locale files, 9 unit tests passing.
<!-- SECTION:FINAL_SUMMARY:END -->
