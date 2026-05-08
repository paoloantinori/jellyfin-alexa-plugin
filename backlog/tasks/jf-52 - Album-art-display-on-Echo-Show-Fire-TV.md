---
id: JF-52
title: Album art display on Echo Show/Fire TV
status: Done
assignee: []
created_date: '2026-05-03 13:38'
updated_date: '2026-05-03 18:45'
labels:
  - enhancement
  - ux
  - visual
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Pass album art and media metadata to the Alexa AudioPlayer directive for visual display on Echo Show, Fire TV, and Alexa app. Inspired by official ASK SDK documentation.

Currently the plugin streams audio but doesn't send cover art, so screen-capable devices show a generic audio icon instead of the album art.

Implementation:
1. When building AudioPlayer.Play directives, include the metadata object with:
   - title: track/episode title
   - subtitle: artist/album name
   - art: cover art URL from Jellyfin server (Items/{itemId}/Images/Primary)
2. Ensure image URLs are publicly accessible HTTPS (same requirement as audio streams)
3. Apply to all playback intents: PlaySong, PlayAlbum, PlayArtistSongs, PlayPlaylist, PlayFavorites, etc.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Cover art URLs were already implemented in BuildAudioPlayerResponse. Added Subtitle field to AudioItemMetadata with album name (Audio items) or series name (Episode items) for richer Echo Show/Fire TV display.
<!-- SECTION:FINAL_SUMMARY:END -->
