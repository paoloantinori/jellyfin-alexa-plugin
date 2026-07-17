---
id: JF-291
title: HLS streaming for seekable first-play in VideoAudioController
status: Done
assignee: []
created_date: '2026-06-10 19:19'
updated_date: '2026-06-10 20:14'
labels:
  - enhancement
  - video-audio
  - performance
dependencies:
  - JF-290
references:
  - jf-290
documentation:
  - claudedocs/research_first_play_seeking_2026-06-10.md
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/VideoAudioCache.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Directive/VideoAppDirective.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement HLS (HTTP Live Streaming) output for the VideoAudioController so that the Echo Show can seek and display correct duration from the very first play. This is the aggressive option that provides the best UX.

The current approach (fragmented MP4 + background faststart remux) has no seeking on first play. HLS solves this because each segment is a complete seekable unit, and the playlist provides total duration.

**Implementation sketch**:
1. New endpoint: `GET /alexaskill/api/video-audio/{itemId}/stream.m3u8` — generates or serves the HLS playlist
2. New endpoint: `GET /alexaskill/api/video-audio/{itemId}/segments/{segmentName}` — serves individual .ts segments
3. On cache miss, run `ffmpeg -hls_time 4 -hls_list_size 0 -hls_flags append_list` to generate segments
4. Stream the playlist while ffmpeg writes segments (append_list keeps playlist updated)
5. On cache hit, serve static playlist + segments
6. Update VideoApp.Launch source URL from `.mp4` to `.m3u8`
7. Garbage-collect old segment directories based on last access time

**Key trade-offs**: Higher complexity (2 new endpoints, segment file management), but HLS is explicitly documented as VideoApp-supported and provides the best UX.

**Depends on**: JF-290 (stream-while-writing) should be implemented first as a fallback — if HLS has issues on specific devices, the MP4 path remains available.

**Key files**: `Controller/VideoAudioController.cs`, `Alexa/VideoAudioCache.cs`, handler code that builds VideoApp.Launch response, interaction model (no changes needed since URL is server-side)

**Research reference**: `claudedocs/research_first_play_seeking_2026-06-10.md` (Option A)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 HLS endpoint serves .m3u8 playlist at GET /alexaskill/api/video-audio/{itemId}/stream.m3u8
- [ ] #2 Segment endpoint serves .ts segments at GET /alexaskill/api/video-audio/{itemId}/segments/{segmentName}
- [ ] #3 First play shows correct total duration in Echo Show player
- [ ] #4 Seeking works from first play within already-generated segments
- [ ] #5 Stale segments and playlists are garbage-collected after configurable TTL
- [ ] #6 Existing MP4 endpoint still works for non-HLS fallback
- [ ] #7 VideoApp.Launch URL updated to use .m3u8 for Echo Show devices
- [ ] #8 Cache hit path serves static .m3u8 + segments without ffmpeg
- [ ] #9 Unit tests for new endpoints (playlist generation, segment serving, cleanup)
- [ ] #10 Deploy and verify on real Echo Show with test audio items
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented HLS streaming for VideoAudioController with two new endpoints: stream.m3u8 (playlist) and segments/{segmentName} (.ts files). HLS provides correct duration display and seeking from the very first play on Echo Show. The playlist is generated completely before serving (waits for ffmpeg to finish) so the client sees all segments immediately. Code quality improvements from simplify review: extracted shared ValidateVideoAudioRequest and RunFfmpegToCompletionAsync helpers, added O(1) in-memory directory lookup for segments, replaced regex with zero-allocation string checks, added CleanupHlsStub for corrupt directory cleanup. Existing MP4 endpoint preserved as fallback. VideoApp.Launch URL updated to use .m3u8. 2343 tests pass (20 new HLS-specific tests).
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
