---
id: JF-222
title: Add on-the-fly MP4 video generation endpoint (album art + audio)
status: Done
assignee: []
created_date: '2026-05-26 21:02'
updated_date: '2026-05-27 06:05'
labels:
  - enhancement
  - videoapp
  - echo-show
milestone: VideoApp Album Art
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Context
The `VideoApp.Launch` directive gives native Echo Show controls (progress bar, scrubber, elapsed time) but shows a black screen for audio. By generating a single-frame MP4 with album art as H.264 video + audio as-is (MP3 copy), we get both native controls AND album art display.

## What to build
A new API controller endpoint (e.g., `GET /alexaskill/api/video-audio/{itemId}`) that:
1. Fetches album art image from Jellyfin (`/Items/{albumId}/Images/Primary`)
2. Fetches audio stream from Jellyfin (`/Audio/{itemId}/stream`)
3. Runs ffmpeg to combine them into a streamable fragmented MP4:
   ```
   ffmpeg -loop 1 -framerate 1 \
     -i album_art.jpg \
     -i audio.mp3 \
     -c:v libx264 -tune stillimage -preset ultrafast -crf 28 \
     -c:a copy \
     -pix_fmt yuv420p -r 1 \
     -f mp4 -movflags frag_keyframe+empty_moov \
     -shortest pipe:1
   ```
4. Streams the output directly to the response (chunked transfer)

## Key details
- Audio is copied as-is (`-c:a copy`) — Echo Show VideoApp accepts MP3, confirmed by testing
- Video is trivially lightweight: 1fps, static image, `stillimage` tune
- Output is fragmented MP4 (`-movflags frag_keyframe+empty_moov`) for progressive streaming without seeking to moov atom
- Resolution: scale album art to 1280x720 (max supported by VideoApp)
- Must work inside the Jellyfin container (ffmpeg must be available)
- Authentication: validate API key from query parameter

## Dependencies
- ffmpeg must be available in the Jellyfin container
- Album art URL is already known via `BaseHandler.GetImageUrl()` pattern
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented VideoAudioController with GET /alexaskill/api/video-audio/{itemId} endpoint. Combines album art + audio into streamable fragmented MP4 via ffmpeg. Uses Jellyfin's IMediaEncoder for ffmpeg path resolution. Supports art fallback chain (item image → album parent → generic parent → black frame). 15 unit tests, all passing. Also archived stale m-3 milestone and 9 related tasks (JF-213 to JF-221).
<!-- SECTION:FINAL_SUMMARY:END -->
