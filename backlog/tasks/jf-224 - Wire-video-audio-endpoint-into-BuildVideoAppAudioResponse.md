---
id: JF-224
title: Wire video-audio endpoint into BuildVideoAppAudioResponse
status: Done
assignee: []
created_date: '2026-05-26 21:03'
updated_date: '2026-05-27 06:23'
labels:
  - enhancement
  - videoapp
milestone: VideoApp Album Art
dependencies:
  - JF-222
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Context
JF-222 creates the video generation endpoint. This task wires it into the existing `BuildVideoAppAudioResponse` so that when `NativeControlsForAudio` is enabled, the VideoApp.Launch directive points to the video-audio endpoint (with album art) instead of the raw audio stream (black screen).

## What to change
1. **`BaseHandler.BuildVideoAppAudioResponse`** — change the `Source` URL from raw audio stream to the new video-audio endpoint:
   - Before: `https://jellyfin.example.com/Audio/{id}/stream?static=true&ApiKey=...`
   - After: `https://jellyfin.example.com/alexaskill/api/video-audio/{id}?ApiKey=...`
2. **Album art resolution** — ensure the endpoint knows the album ID (fetch from item's `AlbumId` property)
3. **Fallback** — if ffmpeg is not available in the container, fall back to raw audio stream (current behavior with black screen)

## Files to modify
- `Alexa/Handler/BaseHandler.cs` — `BuildVideoAppAudioResponse` method
- Possibly a new URL builder method alongside `GetStreamUrl()` / `GetVideoStreamUrl()`

## Depends on
JF-222 (video generation endpoint must exist)
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
Modified BuildVideoAppAudioResponse to use the new video-audio MP4 endpoint URL instead of raw audio stream. Added GetVideoAudioUrl() method. Routing in BuildAudioPlayerResponse now sends initial playback through VideoApp (album art + native controls) while enqueue and resume stay on AudioPlayer. 9 new unit tests verify URL format and routing logic.
<!-- SECTION:FINAL_SUMMARY:END -->
