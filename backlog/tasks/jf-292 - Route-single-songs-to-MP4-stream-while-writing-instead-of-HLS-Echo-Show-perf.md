---
id: JF-292
title: Route single songs to MP4 stream-while-writing instead of HLS (Echo Show perf)
status: Done
assignee: []
created_date: '2026-06-16 08:32'
updated_date: '2026-09-05 09:21'
labels:
  - performance
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
On Echo Show with NativeControlsForAudio enabled, every music play routes through VideoApp and is served via a full HLS segment encode, costing 5-20s per uncached song. Observed in 2026-06-16 morning server logs: item dad2dffb ~20s, 5b0cbe16 ~17s, others 5-8s; ffmpeg stderr dominated ~1500/2173 log lines.

Root cause (verified): BaseHandler.GetVideoAudioUrl (Alexa/Handler/BaseHandler.cs:393-394) returns an `.m3u8` HLS URL for ALL non-audiobook items, so single songs hit StreamHlsVideoAudio (Controller/VideoAudioController.cs:247 — full HLS segment encode + blocks until first 4s segment is written, 4-10s). BuildVideoAppAudioResponse (BaseHandler.cs:657-674) routes audiobooks to GetAudiobookVideoAudioUrl (line 667) and everything else to GetVideoAudioUrl (line 672). Triggered when NativeControlsForAudio is on (BaseHandler.cs:535→541).

There is already a cheaper MP4 stream-while-writing endpoint, StreamVideoAudio (VideoAudioController.cs:92-232), which returns in ~10ms and has a background faststart remux (RemuxToFaststartAsync) that makes the file seekable for subsequent plays. It is currently dead code for music.

Goal: single songs use the MP4 stream-while-writing path; HLS is reserved for multi-chapter audiobooks (which genuinely need the HLS seek bar). The seek bar for music must still work after the change — verify on Echo Show that a fragmented MP4 + faststart supports seeking (byte-range). Note: the MP4 path still re-encodes audio to AAC (separate task addresses that).

This is the highest-impact perf fix: cuts a cold song play from 5-20s to ~sub-second first byte.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Single Audio (song) plays on Echo Show (NativeControlsForAudio on) use the MP4 stream-while-writing endpoint (StreamVideoAudio), not HLS (StreamHlsVideoAudio)
- [ ] #2 Audiobook plays still use the HLS concat path (GetAudiobookVideoAudioUrl) — unchanged
- [ ] #3 Seek bar still works while a song plays on Echo Show (manual verification on device)
- [ ] #4 First-byte time for an uncached song drops to sub-second; logs no longer show 'Audiobook HLS encoding complete' for song plays
- [ ] #5 Unit tests updated: BuildVideoAppAudioResponse routes Audio items to the MP4 URL and AudioBook items to the HLS URL; existing VideoAudioController tests still pass
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
REVERTED on 2026-06-16 (commit 8e61299) after on-device Echo Show test: routing songs to the MP4 stream-while-writing endpoint produced a BLACK SCREEN + no audio.

ROOT CAUSE: the MP4 path muxes with `-movflags frag_keyframe+empty_moov` (live fragmented MP4). ExoPlayer's VideoApp CANNOT play a raw fragmented MP4 with empty_moov served over plain HTTP — it expects complete/progressive files or HLS, not an incrementally-written fMP4. The faststart .fs.mp4 (the playable, moov-at-front version) only exists AFTER the full encode+remux, so it can't provide instant playback.

EVIDENCE: deployed v0.7.0.0 (JF-292 active), played '1979' by Smashing Pumpkins -> server logged ffmpeg args with `frag_keyframe+empty_moov`, served FileStreamResult of the live fragmented MP4 -> Echo Show black screen. Confirmed via logs (corr=f492736d).

STATE NOW: GetVideoAudioUrl reverted to .m3u8 (songs via HLS again). JF-293 (audio copy), JF-294, JF-295 retained and deployed. Songs play again via HLS.

TO FINISH THIS TASK: a different approach is needed — either (a) keep HLS for Echo Show and optimize it (audio copy from JF-293 already speeds the encode; investigate faster first-segment / shorter segments), or (b) find a streamable container ExoPlayer VideoApp accepts live (proper fMP4 CMAF with init segment? needs validation). Do NOT assume MP4 stream-while-writing works on Echo Show — it does not.
2026-09-05 closure measurements (live server): the latency goal of this task is ALREADY MET with routing unchanged, by the shipped JF-293 audio-copy encode plus the `-g 1` keyframe fix. Cold HLS playlist generation for songs measured 0.42s / 0.65s / 1.97s (three songs, 140-209s long), first segment 0.01s, warm playlist 3-10ms. The 2026-06 5-20s numbers in the Description are stale. Conclusion: HLS stays for songs (option (a) above, already shipped); the MP4 stream-while-writing route remains dead per the June on-device black-screen evidence, and no re-attempt is warranted without a validated CMAF-style container. The only code change in this final scope is the log-label fix: MonitorFfmpegHlsAsync (shared by the song and audiobook HLS paths) gained a `label` parameter ("Song" at the StreamHlsVideoAudio call site, "Audiobook" at the StreamHlsAudiobook call site) so song encodes stop logging as "Audiobook HLS encoding ..." (the AC#4 triage complaint). No routing, argument, or timeout behavior changed; the expectedChapterCount INCOMPLETE branch still fires only for audiobooks because only that path writes encode-metadata.json.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed 2026-09-05 (commit 983fdf8a) on live measurements, not on the originally-planned routing change. The latency goal (cold song play from 5-20s down to sub-second) is ALREADY MET by the fixes retained after the June revert: JF-293 audio-copy plus the -g 1 keyframe fix. Measured on the live server 2026-09-05 (three songs 140-209s, cache cleared per item): cold HLS playlist 0.42s / 0.65s / 1.97s, first segment 0.01s, warm playlist 3-10ms. Routing unchanged: songs stay on HLS (the seek bar keeps working by construction, no on-device regression possible); the MP4 stream-while-writing approach stays dead per the 2026-06-16 black-screen evidence (ExoPlayer VideoApp rejects fMP4 with empty_moov over plain HTTP; corr=f492736d). The only code change that landed: the shared ffmpeg monitor's log messages now carry a per-path {Label} (Song / Audiobook) so song encodes stop being logged as audiobooks (AC#4's residual); scoped code-review verified it behavior-neutral, suite 3243/3243. Deploy rides the next batch; the 'Song HLS encoding complete' line gets its live check then. AC#1/#5 are void (the MP4 routing they describe was reverted); AC#2/#3 hold by construction.
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
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
