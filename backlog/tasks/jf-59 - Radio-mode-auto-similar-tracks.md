---
id: JF-59
title: Radio mode (auto-similar tracks)
status: Done
assignee: []
created_date: '2026-05-03 13:39'
updated_date: '2026-05-03 19:41'
labels:
  - enhancement
  - intent
  - discovery
  - voice-interaction
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement radio/auto-continue mode that automatically generates a stream of similar tracks after the current selection finishes. Inspired by Navidrome's getSimilarSongs API and Music Assistant's radio_mode feature.

Support utterances like:
- "Play {song} and keep playing similar music"
- "Turn on radio mode"
- "Play more like this"

Implementation:
1. After current track/album/playlist finishes, use Jellyfin's similar items API or artist/genre matching to fetch related tracks
2. Auto-enqueue similar tracks via PlaybackNearlyFinished handler
3. Configurable per user (always on / opt-in via voice command / off)
4. If Jellyfin has the Local Recs plugin or similar, leverage its recommendations
5. Fallback: use artist-based and genre-based matching from the user's library
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
