---
id: JF-290
title: Stream-while-writing for instant first-play in VideoAudioController
status: Done
assignee: []
created_date: '2026-06-10 19:19'
updated_date: '2026-06-10 19:44'
labels:
  - enhancement
  - video-audio
  - performance
dependencies: []
documentation:
  - claudedocs/research_first_play_seeking_2026-06-10.md
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/VideoAudioCache.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Improve the VideoAudio endpoint to start streaming the MP4 to the Echo Show immediately while ffmpeg is still generating it, instead of waiting for the entire file to be written to disk first. This eliminates the 10-30s first-play delay for audiobooks and long content.

Currently the controller waits for ffmpeg to finish writing the entire fragmented MP4 before serving it via PhysicalFile. The fix: open the file for reading with `FileShare.ReadWrite` while ffmpeg writes, and stream it to the client via `FileStreamResult` with chunked transfer encoding.

After streaming completes, the existing background remux to `.fs.mp4` provides full seeking on subsequent plays. This is the conservative improvement — HLS (Option A) is tracked in a separate task for full first-play seeking.

**Key files**: `Controller/VideoAudioController.cs`, `Alexa/VideoAudioCache.cs`, tests in `Tests/Controller/VideoAudioControllerTests.cs`

**Research reference**: `claudedocs/research_first_play_seeking_2026-06-10.md` (Option B)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 First play starts streaming within 2-5 seconds (first ffmpeg fragment), not after full encode
- [ ] #2 Cache hit path (faststart .fs.mp4) continues to serve via PhysicalFile with range processing
- [ ] #3 Per-item locking still prevents concurrent ffmpeg processes for the same item
- [ ] #4 Background remux to .fs.mp4 still works — seeking available on second play
- [ ] #5 Client disconnection (RequestAborted) kills ffmpeg process and cleans up partial file
- [ ] #6 ffmpeg stderr is still logged for debugging
- [ ] #7 Existing unit tests pass without modification to assertions
- [ ] #8 New test: verify FileStreamResult is returned for cache-miss path with FileShare.ReadWrite
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented stream-while-writing for VideoAudioController. On cache miss, ffmpeg starts and the MP4 is streamed to the client immediately via FileStreamResult (FileShare.ReadWrite) instead of waiting for ffmpeg to finish. Fixes from simplify review: (1) no double-dispose race (FileStreamResult owns stream exclusively), (2) ffmpeg process cleaned up on all failure paths, (3) 5-minute timeout restored on MonitorFfmpegAndRemuxAsync, (4) spin-wait checks ffmpeg.HasExited to fail fast, (5) RemuxToFaststartAsync refactored to use shared StartFfmpegProcess, (6) dead DeleteCacheFile removed. Cache hit path unchanged (PhysicalFile with range processing). 34 VideoAudio tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->

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
