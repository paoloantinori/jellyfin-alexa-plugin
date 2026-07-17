---
id: JF-223
title: Add hybrid cache layer for generated video-audio files
status: Done
assignee: []
created_date: '2026-05-26 21:02'
updated_date: '2026-05-27 06:58'
labels:
  - enhancement
  - videoapp
  - performance
milestone: VideoApp Album Art
dependencies:
  - JF-222
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Context
On-the-fly ffmpeg transcoding adds 1-3s latency per track. Caching eliminates this after first play.

## What to build
A hybrid caching layer for the video-audio endpoint:

1. **Cache key**: `{itemId}_{albumArtLastModified}` — invalidates when album art changes
2. **Cache location**: Jellyfin cache directory inside the container (e.g., `/config/data/plugins/AlexaSkill_<ver>/video_cache/`)
3. **On first request**: Generate via ffmpeg, write to cache file, stream to client simultaneously (tee the output)
4. **On subsequent requests**: Serve cached file directly (zero latency)
5. **Cache eviction**: LRU or size-based (configurable max cache size, e.g., 2GB)
6. **Cache invalidation**: Check album art last-modified timestamp via Jellyfin API before serving

## Key details
- Cache files are MP4 with album art + audio
- Typical size: audio_size + ~300KB video overhead (1fps static image)
- For 10K tracks at ~8MB average: ~80GB full cache, but most users play a subset
- Start with a simple file-based cache, no external dependencies
- Serve via `PhysicalFileResult` with proper content-type `video/mp4`

## Depends on
JF-222 (video generation endpoint)
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
Added file-based hybrid cache for ffmpeg-generated MP4 video-audio files. Cache key uses {itemId}_{artModifiedTicks} for automatic invalidation on album art changes. LRU eviction by total size (configurable 2GB default). ffmpeg output is teed to both response and cache simultaneously for zero-delay first play. Added VideoAudioCacheSizeMB config with UI. 12 new cache tests (33 total VideoAudio tests passing).
<!-- SECTION:FINAL_SUMMARY:END -->
