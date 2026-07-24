---
id: JF-293
title: 'Stream-copy audio (-c:a copy) for single-item video-audio encode'
status: Done
assignee: []
created_date: '2026-06-16 08:32'
updated_date: '2026-06-16 09:32'
labels:
  - performance
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The single-item video-audio ffmpeg encode re-encodes audio to AAC even when the source is already MP3/AAC, adding ~3-10s per song. Verified: AudioCodecArgs = ["-c:a","aac","-b:a","128k"] (Controller/VideoAudioController.cs:904), used by BuildFfmpegArguments (MP4 path, line 890) AND BuildHlsFfmpegArguments (single-item HLS, line 950). The audiobook path correctly uses ["-c:a","copy"] (line 1052). Observed logs show [mp3float] decoder = MP3 sources, which stream-copy cleanly into MP4/MPEG-TS.

Goal: single-item encode stream-copies audio when the source codec is MP3 or AAC; fall back to AAC re-encode for incompatible codecs (FLAC/Opus/WMA/etc.) to avoid playback failure. Detect the source codec (probe the Jellyfin item's container/codec) and log the decision.

Complements the MP4-routing task — together they cut a cold song play from 5-20s to <2s. Independent of that task (applies -c:a copy to whichever single-item path is active) but most impactful once songs use the MP4 path.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Single-item video-audio encode uses -c:a copy when the source audio codec is MP3 or AAC
- [ ] #2 Incompatible source codecs (e.g. FLAC, Opus, WMA) fall back to -c:a aac re-encode with no playback failure
- [ ] #3 Source codec is detected and the copy-vs-transcode decision is logged
- [ ] #4 Encode time for a typical MP3 song drops substantially (verify via 'encoding complete' log timestamp delta)
- [ ] #5 Unit tests cover both the copy branch and the fallback branch
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added codec-aware audio stream-copy to the single-item video-audio path. New CopyCompatibleAudioCodecs {mp3,aac}, ResolveSourceAudioCodec (via IMediaSourceManager), and BuildAudioCodecArgs selector; mp3/aac sources now remux with -c:a copy (instant), others fall back to AAC transcode. Both StreamVideoAudio (MP4) and StreamHlsVideoAudio updated with codec logging; audiobook path untouched. VideoAudioController gained an IMediaSourceManager DI constructor ([ActivatorUItilitiesConstructor]) with a 4-arg test ctor. 13 new tests (copy + fallback branches, Theory). Build green (0 warnings), 2397 tests pass. Committed 347111a. Runtime DI resolution of IMediaSourceManager + on-device verification pending (deploy).
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
