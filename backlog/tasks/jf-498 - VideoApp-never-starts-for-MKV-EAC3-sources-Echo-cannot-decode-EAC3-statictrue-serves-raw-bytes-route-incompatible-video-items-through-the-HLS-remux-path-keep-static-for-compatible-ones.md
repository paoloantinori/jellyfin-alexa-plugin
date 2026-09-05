---
id: JF-498
title: >-
  VideoApp never starts for MKV/EAC3 sources (Echo cannot decode EAC3;
  static=true serves raw bytes): route incompatible video items through the HLS
  remux path, keep static for compatible ones
status: Done
assignee: []
created_date: '2026-09-05 19:13'
updated_date: '2026-09-05 20:32'
labels:
  - tv
  - video
  - echoshow
  - transcoding
dependencies: []
references:
  - corr=d9f848a7
  - JF-324
  - JF-292
  - VideoAudioController
  - CLAUDE.md Echo Show video constraints
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-05 21:10 device test (corr=d9f848a7): the whole PlayNextEpisode chain is correct on-device (intent selected, series resolved via the catalog ER_SUCCESS_MATCH, NextUp latest-fallback resolved the episode, VideoApp.Launch directive returned), but the video NEVER STARTED. Root cause: the episode file is MKV with H.264 High video + EAC3 (Dolby Digital Plus, one variant Atmos) audio; the response serves /Videos/{id}/stream?static=true (raw bytes). The Echo Show's ExoPlayer advertises only H.264 (H_264_42/41) and does not decode EAC3 for third-party VideoApp playback, so the audio renderer cannot initialize and playback never begins. This is a LATENT bug of the whole video path, not the new intent: PlayVideo/StartOver/ContinueWatching/PlayEpisode-explicit all serve the same static URL; movies presumably worked because Paolo's movie sources are compatible (to confirm: play a movie via the skill on device).

FIX SHAPE (reuses the existing video-audio HLS machinery, the JF-292 family in reverse): when the resolved video item's audio codec is not Echo-compatible (eac3/ac3/truehd/dts/dts-hd; keep the list conservative and data-driven from the item's MediaStreams) OR the container is not directly playable (mkv/webm for VideoApp), route the VideoApp.Launch to the EXISTING HLS video-audio endpoint family (Controller/VideoAudioController): video COPY (the sources are already H.264: remux, not transcode) + audio transcode to AAC, hls_time 4, -g at keyframe cadence suitable for full-framerate video (NOT the 1fps audiobook trick), stream-while-writing playlist (first segment in seconds, encode continues in background - the JF-292 measurements: 0.4-2s first playlist). The audiobook path's per-item cache + eviction + token machinery applies unchanged (a 45min episode at ~2.5Mbps is ~850MB; consider the cache budget implications, JF-310/421/428 bounds already exist). Compatible sources (mp4/h264/aac) keep today's static URL: zero regression for movies.

Deliverables: a shared 'resolve VideoApp source for item' helper (codec/container probe -> static vs HLS URL) used by ALL VideoApp launch sites (PlayVideo, PlayEpisode explicit, PlayNextEpisode, StartOver, ContinueWatching, YesIntent routes); unit tests with both source shapes; device verification with the Adolescence episodes (EAC3) and one movie (regression).
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes (2026-09-05, first cut)

**Probe (shared policy)**: `Alexa/Util/VideoAppStreamPolicy.cs`. `Decide(videoCodec, audioCodec, container)` is a pure function over the item's first video/audio stream codecs. Routing: audio in {eac3, ac3, truehd, dts, dtshd, dts-hd} AND video == h264 -> HLS remux; video codec known and != h264 -> keep static + LogWarning naming the codec (no video transcode path in this cut, per task scope); video codec unknown -> static (the probe may only ADD the remux route, never break today's launch). DOCUMENTED mkv+aac decision: static. The container deliberately does NOT trigger the remux: ExoPlayer extracts Matroska natively and the evidenced failure (corr=d9f848a7) is codec-level (no EAC3 decoder), so an h264+aac MKV keeps working behavior instead of paying a remux. The container parameter stays in the signature so a future container rule has a place to land. Codec extraction (`ExtractCodecs`) skips subtitle streams; case-insensitive.

**Handler wiring (the choke point)**: `BaseHandler.GetVideoAppLaunchUrl(item, user)` probes via `BaseItem.GetMediaStreams()` (virtual; failures degrade to static) and returns either the static `/Videos/{id}/stream?static=true` URL or `GetEpisodeVideoAudioUrl` (token minted via StreamTokenHelper, same JF-309 machinery as the song path). All 11 Movie/Episode launch sites now route through it:
- PlayVideoIntentHandler (movie/episode search)
- PlayEpisodeIntentHandler explicit episode path
- BaseHandler.PlayNextUpEpisodeAsync (NextUp + latest fallback)
- ResumeIntentHandler (server-side progress fallback, Movie/Episode)
- ContinueWatchingIntentHandler
- StartOverIntentHandler
- AplUserEventHandler selectItem/carouselTap movie launch
- SearchMediaIntentHandler.PlayItem (video branch)
- YesIntentHandler.PlayVideo (disambiguation confirm)
- PlayRandomIntentHandler (movie/episode branch)
- RecommendIntentHandler (movie branch)

Sites deliberately NOT wired (not Movie/Episode launches): BuildChannelLaunchResponseAsync (live TV, resolver URL), BuildVideoAppAudioResponse (audio items), BuildAudiobookResumeResponse (audiobook concat).

Side effect of the wiring: 5 sites (PlayEpisode, SearchMedia video branch, YesIntent PlayVideo, PlayRandom video branch, Recommend movie branch) previously served the /Audio/{id}/stream URL for video items inside a VideoApp directive (unplayable); the helper fixes them to the /Videos/ static URL (or the remux) as a consequence of being routed through the single decision point.

**Endpoint**: `Controller/VideoAudioController.StreamHlsEpisode` at `GET alexaskill/api/video-audio/episode/{itemId}/stream.m3u8` (token-validated like the other endpoints; serves movies too). Core mirrors `StreamHlsVideoAudioCore` (per-item cache dir + art ticks key, per-item lock, first-segment wait, partial playlist served stream-while-writing, background `MonitorFfmpegHlsAsync` with label "Episode"). ffmpeg input is the item's own static stream (`{ServerUrl}/Videos/{id}/stream?static=true`, the same no-auth shape the song path uses for /Audio/; both endpoints carry identical auth attributes at Jellyfin 10.11.8, verified in the tag source). Arguments (`BuildEpisodeHlsFfmpegArguments`):

```
-i {videoUrl} -map 0:v:0 -map 0:a:0
  -c:v copy -g 48
  -c:a aac -b:a 192k        (or -c:a copy when the source audio is mp3/aac, BuildAudioCodecArgs-style selection)
  -hls_time 4 -hls_list_size 0 -hls_flags append_list -hls_segment_type mpegts
  -hls_segment_filename seg_%04d.ts -hls_base_url /alexaskill/api/video-audio/{itemId}/segments/
  stream.m3u8
```

- Explicit v:0/a:0 mapping drops MKV subtitle streams (PGS/SRT cannot be muxed into MPEG-TS and would fail the encode).
- `-g 48` is INERT under `-c:v copy` (encoder option; documented in the code): with copy the muxer cuts at the SOURCE keyframes (4-10s typical); it documents the assumed cadence and becomes live if a transcode is ever added. Measured on the exact arg set (ffmpeg 8.1.2, 60s h264+eac3 MKV): first 4s segment on disk in 0.31s, full remux in 3.95s, ENDLIST written at completion (verified: only `omit_endlist` suppresses it, `append_list` does not).
- No `-shortest`: both output streams come from one finite input (-shortest exists in the JF-292 recipe to bound the infinite art input).
- 192k audio (vs the 128k music path): TV EAC3 is commonly 5.1.

**Cache/eviction**: reuses `StartFfmpegProcessGatedAsync` (encode gate + JF-428 pin-before-sweep) with a dedicated `EstimateEpisodeEncodeBytes`: 1280MB/h with 1h floor and round-UP (video bytes are copied, ~20x the 64MB/h art+audio path; a 45min episode reserves ~850-950MB; the default 2048MB cap holds ~1.5h of episodes; the JF-428 half-cap floor keeps in-progress dirs from being wiped). Stale-cache handling: a cached playlist WITHOUT ENDLIST is valid only while an episode encode is active (`_activeEpisodeEncodes`, set before ffmpeg starts so the lock-free fast path cannot race a live encode); otherwise it is the debris of an interrupted encode and is cleaned up + re-encoded (the song path has no such check; episodes are too big to leave stalled mid-episode). `GetSegment` skips audiobook position tracking for episode items (`_episodeHlsItems` registry) so episode segment fetches do not grow keys in the persisted audiobook-positions file.

**LastPlayed recording**: `LastPlayedResponseInterceptor` now extracts the item GUID from BOTH `/Videos/{guid}/stream` and `/alexaskill/api/video-audio/episode/{guid}/stream.m3u8` sources; audio-via-VideoApp and audiobook concat URLs remain skipped (existing tests pin that).

**Tests** (+40, suite 3282 -> 3322, all green): `Unit/VideoAppStreamPolicyTests` (probe matrix incl. mkv+eac3/mp4+aac/mkv+aac, container independence, non-h264 warning, unknown-codec conservatism, stream extraction); `Handler/PlayVideoIntentHandlerTests` (wiring: EAC3 movie -> episode remux URL with token; h264+aac movie -> static /Videos/ URL; via a Movie subclass overriding the virtual GetMediaStreams so the REAL probe runs); `Controller/VideoAudioControllerTests` (arg builder: copy/AAC-192k/-g 48/hls_time 4/mapping/no -shortest; audio-copy selection; estimate theory; 401 without token; 400 bad GUID; cache-miss serve with fake ffmpeg asserting the /Videos/ input URL and partial-playlist token injection); `Unit/LastPlayedResponseInterceptorTests` (episode URL records).

**Verification**: `dotnet build` (Debug + Release) 0 errors 0 warnings; `dotnet test` full suite 3322/3322 passed. /simplify pass applied (ResolveSourceAudioCodec/VideoCodec deduplicated into ResolveSourceCodec; ValidateEpisodeCacheAsync checks the in-memory active flag before reading the playlist file). Code review gate run over the uncommitted diff (all five angles, no finding at or above the >= 80 reporting bar; one watch item below).

**Remaining for Done**: on-device verification (Adolescence EAC3 episodes via PlayNextEpisode + one compatible movie for regression) requires a deploy, which was out of scope for this implementation pass. Watch item for that test: the remux's ffmpeg input (`/Videos/{id}/stream?static=true`, no api_key) is auth-identical to the production-proven `/Audio/` song input at the 10.11.8 tag, but the device run is what proves it on the live server.

## Review dispositions (2026-09-05, second pass: formal-review C1 + I1 applied)

**C1a PLAYBACK PIN (critical, applied)**: the JF-428 write pin releases when ffmpeg exits, which for a completed remux is minutes into a multi-hour watch; the monitor's post-exit `EvictIfNeeded` then targeted the full cap and, when the just-encoded dir alone exceeded it (every older entry already evicted), deleted the dir the client was still fetching segments from: 404s mid-playback plus a from-zero re-encode. Fix shape: the existing JF-320 in-memory serve-recency ledger (`VideoAudioCache._lastAccessUtc`, refreshed by `RecordAccess` on EVERY playlist fetch (`GetCachedHlsPlaylist`), segment fetch (`FindSegmentPath`), and mp4 cache hit (`GetCachedFile`); these three hooks are shared by the episode, song, and audiobook serving paths, so no new wiring sites were needed) now ALSO acts as an eviction EXEMPTION, not just LRU ordering: `EvictIfNeededCore` skips any entry whose recorded serve is inside `PlaybackEvictionExemptionTtl` (10 minutes; internal-settable test seam in the KeyedOneShotDebounce style), exactly like a JF-428 pin. A playing client fetches every few seconds, so the exemption only expires after the client stops; an exempt entry alone exceeding the cap is TOLERATED (documented in code: disk temporarily over budget beats breaking playback) and the final sweep warning now names "recently-served entries" among the tolerated over-target causes. Protects all three size classes (episode remux, song HLS/mp4, audiobook concat).

**C1b BITRATE-AWARE ESTIMATE (critical, applied)**: `EstimateEpisodeEncodeBytes(runtimeTicks, totalBitRateBps = null)` now scales from the item's combined FIRST-video + FIRST-audio `MediaStream.BitRate` when available (`ResolveTotalMediaBitrateBps`, same `_mediaSourceManager.GetMediaStreams` shape as `ResolveSourceCodec`; null on manager-unavailable/no-BitRate/read-failure): bytes = bits/s / 8 * seconds, +10% container overhead (`EpisodeContainerOverheadMargin`), so a 10Mbps Blu-ray remux reserves ~4.95GB/h instead of the flat 1280MB/h. Absent/zero bitrate keeps today's flat 1280MB/h round-up path; a present bitrate with unknown runtime floors at the flat one-hour rate. Summing the SOURCE audio bitrate slightly over-reserves when it transcodes down to AAC 192k; conservative is the right direction for the JF-428 headroom. The flat-path theory tests are unchanged and still pass (optional param defaults null).

**I1 DEBRIS RE-ENCODE DOUBLED PLAYLIST (important, applied)**: when `VideoAudioCache.Cleanup`'s all-or-nothing recursive delete failed (locked/permission-denied dir; IOExceptions swallowed) the debris playlist survived validation and ffmpeg ran with `append_list` over it, baking a doubled playlist into the cache. Fix: `VideoAudioCache.DeleteHlsEncodeDebris(itemId, artModifiedTicks)` is called inside the episode path's per-item lock immediately BEFORE ffmpeg starts (before the active-flag TryAdd); it deletes the target `stream.m3u8` and `seg_*.ts` files individually, best-effort (warning + proceed on each failure), leaving unrelated files and the dir in place. Per-file deletion is the point: one undeletable file no longer fails the whole cleanup, and specifically the PLAYLIST (the actual append_list hazard) gets deleted whenever it is deletable. `CleanupHlsStub` semantics untouched for the other paths.

**Tests (+14, suite 3322 -> 3336, all green)**: cache playback pin (`EvictIfNeeded_RecentlyServedHlsDir_SurvivesEvenWhenAloneOverCap`: served dir alone over cap is never the victim while the cold sibling evicts, ancient atimes on both so only the serve record can explain survival; `EvictIfNeeded_ServeRecordPastTtl_EntryEvictedNormally`: TTL expiry via the internal seam); debris deletion (`DeleteHlsEncodeDebris_RemovesPlaylistAndSegments_KeepsOtherFiles`, `_MissingDirectory_IsNoOp`, `_DeniedDeletion_WarnsAndDoesNotThrow` with the mode-bit + non-root guard of the existing permission tests); bitrate resolver (`ResolveTotalMediaBitrateBps_SumsFirstVideoAndAudioStreams` incl. second-audio/subtitle exclusion, `_NoBitrateOrNoManager_ReturnsNull`); estimate theories (bitrate-present 10Mbps/45min, 10Mbps/1h, 2.884Mbps/1h; absent/zero bitrate and unknown-runtime flat fallbacks); endpoint-level invariant `StreamHlsEpisode_DebrisBeforeEncode_FfmpegStartsOverCleanTarget` (fake ffmpeg snapshots the dir at start: no stale playlist/segment exist when the encode begins, and the served playlist carries only the fresh segment). Test-design note, stated for honesty: on Linux the discriminating failure for I1 (whole-dir delete fails while per-file deletes succeed) is not constructible without tripping the separately tracked JF-499 W4 (Cleanup's missing UnauthorizedAccessException catch), so the endpoint test pins the layered invariant while the three unit tests discriminate on the new method directly.

**Verification**: `dotnet build` Debug AND Release: 0 errors, 0 warnings. `dotnet test` full suite: 3336/3336 passed, 0 failed, 0 skipped (non-root, so the permission-denial branches genuinely executed). /simplify pass on the C1+I1 hunks applied: `TryGetMediaStreams` extracted as the one shared media-streams reader (ResolveSourceCodec + ResolveTotalMediaBitrateBps both use it), the redundant `File.Exists` guard before the playlist delete dropped (File.Delete is a no-op on a missing file), and the playback-pin cutoff hoisted to a single per-sweep instant. One skip noted: the episode encode-start path now makes three `GetMediaStreams` reads (two pre-existing codec resolvers + the bitrate resolver); collapsing them means re-plumbing the first-cut codec-resolver call sites, out of this pass's scope, and the cost is one query per play start. DoD #9/#10 (formal /simplify + /code-review high over the FULL stream) remain for the task's Done transition.

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented, reviewed, deployed and live-verified 2026-09-05 (commit fe34989a, DLL 8b1d8b3a). Shared VideoAppStreamPolicy (audio-incompatible {eac3,ac3,truehd,dts family} + h264 -> HLS remux; known non-h264 -> static + named-codec warning; container never triggers; probe failure/unknown -> static) consumed by BaseHandler.GetVideoAppLaunchUrl at 11 launch sites (5 sites previously served /Audio/ URLs for video items inside VideoApp, which could never play; now correct). New episode HLS endpoint (video copy, audio AAC 192k or copy, hls_time 4, subtitles unmapped) reusing the cache/lock/gate/monitor machinery. Review fixes applied: C1 playback pin (serve ledger exempts entries served within 10 min from eviction) + bitrate-aware encode reserve; I1 in-lock debris deletion before ffmpeg. Reviewer verified the no-auth ffmpeg input against the Jellyfin tag + live probes. LIVE-VERIFIED: The Bear (h264+eac3) routes to the .m3u8 with token, the remux runs (video copy + eac3->AAC), the event playlist grows in background (172 segments), seg_0000.ts (2.6MB) serves in 0.16s; a movie (h264+aac, mp4) correctly keeps the static URL. BOUNDARY: HEVC-video sources (all of Adolescence, some HotD/Silo/The Bear episodes) keep static with a warning - the video transcode path is deliberately out of this first cut, filed as the follow-up task. Watch items W1-W4 filed as JF-499. Suite 3336/3336; gates: /simplify (both workers) + formal code-review (C1-90, I1-80 applied).
<!-- SECTION:FINAL_SUMMARY:END -->
