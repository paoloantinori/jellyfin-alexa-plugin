---
id: JF-22
title: Add cover art to AudioPlayer responses
status: Done
assignee: []
created_date: '2026-05-01 06:21'
updated_date: '2026-05-02 05:20'
labels:
  - feature
  - ux
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
FEATURE from upstream Python project: Add album art / cover art to audio playback directives.

Implementation:
- In audio playback handlers (PlaySong, PlayAlbum, PlayArtistSongs, PlayFavorites, PlayPlaylist, PlayLastAdded), include cover art metadata in the AudioPlayerPlayDirective
- Get image URL from Jellyfin: Items/{itemId}/Images/Primary
- AudioPlayerPlayDirective supports Stream.Metadata.Art (cover art URL) and BackgroundImage
- Small change per handler, big UX improvement (album art shows on Echo Show, Fire TV, etc.)

Reference: infinityofspace/jellyfin_alexa_skill PR #11 (merged)
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
# JF-22: Add Cover Art to AudioPlayer Responses

## Exploration Summary
- `ResponseBuilder.AudioPlayerPlay` has NO metadata overload — must construct `AudioPlayerPlayDirective` manually
- Alexa.NET types: `AudioItemStream` (Url, Token), `AudioItemMetadata` (Title, Subtitle, Art, BackgroundImage), `AudioItemSources` (Sources list), `AudioItemSource` (Url)
- Image URL: `{ServerAddress}/Items/{itemId}/Images/Primary?api_key={token}`
- 20 call sites across 12 handlers need modification

## Implementation Plan

### Step 1: Add helpers to BaseHandler
- Add `GetImageUrl(string itemId, Entities.User user)` — mirrors existing `GetStreamUrl` pattern
- Add `BuildAudioPlayerResponse(PlayBehavior, string url, string token, BaseItem item, Entities.User user)` — constructs `AudioPlayerPlayDirective` with metadata

### Step 2: Replace all ResponseBuilder.AudioPlayerPlay calls
Handlers to modify (20 call sites):
- PlaySongIntentHandler (1), PlayAlbumIntentHandler (1), PlayArtistSongsIntentHandler (1)
- PlayPlaylistIntentHandler (1), PlayFavoritesIntentHandler (1), PlayLastAddedIntentHandler (1)
- PlayChannelIntentHandler (1), LaunchRequestHandler (2), PlayIntentHandler (2)
- ResumeIntentHandler (1), NextIntentHandler (1), PreviousIntentHandler (1)
- StartOverIntentHandler (1), YesIntentHandler (4), PlaybackNearlyFinishedEventHandler (1)

### Step 3: Write tests for cover art in AudioPlayer responses
### Step 4: Run /simplify
### Step 5: Commit
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added GetImageUrl and BuildAudioPlayerResponse helpers to BaseHandler

Replaced all 20 ResponseBuilder.AudioPlayerPlay call sites across 12 handlers with cover art-enabled version

AudioPlayer responses now include AudioItemMetadata with Title, Art, and BackgroundImage from Jellyfin image API

Added offsetInMilliseconds support for resume scenarios

Simplify review: extracted duplicate image URL computation, made BaseItem parameter nullable, removed null-forgiving operator

16 unit tests added verifying cover art metadata, image URLs, offsets, and null handling
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->
