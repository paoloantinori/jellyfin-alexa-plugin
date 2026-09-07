---
id: JF-507
title: >-
  Audio-only resume of EAC3 video items plays 1ms and dies (raw EAC3 track
  unplayable by AudioPlayer) + INVALID_RESPONSE outputSpeech in the failure
  event chain
status: In Progress
assignee: []
created_date: '2026-09-06 15:24'
updated_date: '2026-09-07 05:33'
labels:
  - video
  - audio
  - resume
  - transcoding
dependencies: []
references:
  - corr=f0240020
  - corr=e54b0532
  - JF-498
  - JF-505
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the 2026-09-06 Dot session resume test (17:12:00 corr=f0240020): accepting the resume offer for The Bear episode 'Ribs' on a screenless Echo produced an AudioPlayer.Play with /Audio/{episodeId}/stream?static=true. Playback started and died after 1ms (context in the following ExceptionEncountered: offsetInMilliseconds=1, playerActivity=STOPPED): the episode's audio track is EAC3 (the same source as JF-498's video black-screen), served raw, and the Dot's AudioPlayer cannot decode EAC3. The audio-only resume of EAC3 video items needs transcoding (route through the video-audio HLS machinery or at least an AAC transcode of the audio track). ALSO in the same chain: System.ExceptionEncountered INVALID_RESPONSE 'The following directives are not supported: Response may not contain an outputSpeech' (cause requestId 970b119f): some response in the AudioPlayer event chain after the failed playback carried outputSpeech; find the emitting handler (per the postmortem rule: read the cause request, search backwards) and fix it to the keep-alive/ack shape. NOTE for JF-505: the resume-yes path already launches AUDIO (not VideoApp) on screenless devices, so JF-505's video-gate scope is the direct video intents; this task covers making that audio resume actually playable.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes (2026-09-07)

### Half 1: playable audio resume of EAC3 video items

**Chosen shape: a new AUDIO-ONLY HLS variant of the episode endpoint, served by plugin ffmpeg machinery.** Routes (both under `alexaskill/api/video-audio`, `VideoAudioController`):

- `GET episode/{itemId}/audio.m3u8[?start={ticks}]` -> `StreamHlsEpisodeAudio(Core)`: mirrors `StreamHlsEpisodeCore` (per-item cache dir, per-item lock, first-segment wait, partial playlist, `StartFfmpegProcessGatedAsync`, `MonitorFfmpegHlsAsync` with label `EpisodeAudio`, `ValidateEpisodeCacheAsync`), with `-map 0:a:0` only (no video track), `BuildEpisodeAudioCodecArgs` selection (copy for mp3/aac, else AAC 192k stereo), 10s MPEG-TS segments, and a `-ss {seconds}` input seek when `start > 0`.
- `GET episode/{itemId}/audio-segments/{startTicks}/{segmentName}` -> `GetEpisodeAudioSegment`: a DEDICATED segment route because the cache key is the variant key `{guid}-audio[-{startTicks}]` (`EpisodeAudioCacheKey`), which must not collide with the video remux's bare-itemId directory (the in-memory `RegisterHlsDirectory` lookup is one entry per key string, and the `{key}_*` filesystem scan would cross-match); the start position rides the PATH so the segment route recomputes the same key. Token rules identical (item-scoped HMAC vs the GUID, `?token=` injected into segment lines by `ServePlaylistWithToken`).

**Why not Jellyfin's own `/Audio/{id}/main.m3u8` (investigated live, rejected with evidence).** Probed against the incident episode (`4ae99626-6ff5-d078-f4f0-cd03e8837871`, The Bear "Ribs", h264+EAC3 5.1 MKV) on the live server:

- FOR: it works and DOES transcode. Playlist 200 in 57ms; segment 0 and segment 300 (15min in) each fetched in 0.34-0.7s and ffprobe'd as `aac, channels=2` audio-only; it transcodes EAC3->AAC even WITHOUT an explicit `AudioCodec=aac` param. The modern `/Audio/{id}/universal` route 400s without a full browser-shaped param set, so only the legacy `main.m3u8` is usable.
- AGAINST (decisive): the playlist is `#EXT-X-PLAYLIST-TYPE:VOD` WITH `#EXT-X-ENDLIST` referencing ~777 segments that DO NOT EXIST until first fetch (Jellyfin encodes each segment on demand, one ffmpeg seek+encode per fetch). This exact shape ("VOD playlists referencing missing segments DO fail") is a burned-on-device failure class documented in the repo's audiobook HLS work, and the per-fetch encode latency (~0.3-0.7s per 3s segment, every segment) leaves no headroom exactly when the server is cold or loaded, which is the condition of this very incident (the same chain shows a 2s session-lookup hang, JF-477). The plugin variant instead serves ffmpeg's LIVE growing event playlist (append_list, no ENDLIST until done), the shape proven on Echo hardware, with one encode running ~49x realtime ahead of the player.

**The offset problem, measured.** An audio-only AAC encode of the incident episode (39min) takes 48s in the minix container (~49x realtime). So `offsetInMilliseconds=P` on a from-zero encode cannot work for a resume: the player fetches the segment at P immediately and the JF-503 hold only covers head+2 segments. Decision: the offset moves into the URL (`?start=P`, ffmpeg `-ss` at encode start, directive offset 0). Consequence, documented: the served timeline is RELATIVE to P (the same accepted tradeoff as the audiobook sliced resume), so positions reported by the device for a start-shifted stream are stream-relative, not item-absolute.

**The decision point** is `BaseHandler.ResolveAudioLaunchSource(item, itemId, user, offsetMs)` (+ pure gate `VideoAppStreamPolicy.AudioRequiresTranscode(audioCodec)`: true for the eac3/ac3/truehd/dts family only; the video codec is deliberately NOT consulted because an audio-only stream has no video track; unknown codec keeps static, fail-open). It sits next to `BuildVideoAppAudioResponse`'s screenless degradation (its mirror case) and returns `(Url, OffsetMs)`. Wired sites: `BuildVideoAppAudioResponse` degradation, `YesIntentHandler.HandleResumeConfirmation` (the incident path), `ResumeIntentHandler` tail, `LaunchRequestHandler.HandleSessionQueueResume` (both branches), `JumpToPositionIntentHandler` (its target is item-absolute, exactly what `?start=` wants). NOT wired, deliberately: `SkipForwardBackIntentHandler` (its `current +/- delta` arithmetic reads the device-relative offset, so minting `?start=` from it would resume at the wrong absolute point; rebuilding the raw static URL keeps today's behavior; a correct fix needs the stream-start in state).

Codec/container caveat: for a video item with aac/mp3 audio the raw static URL is kept (task-pinned behavior; note `/Audio/{id}/stream?static=true` on a video item actually serves the WHOLE source container, here `video/x-matroska`, so even aac sources may still fail if the container is MKV; the endpoint re-probes and handles them fine if ever routed there).

### Half 2: INVALID_RESPONSE outputSpeech in the failure chain

**Log evidence is INTACT** (`podman logs` rotated past it, but `/config/log/log_20260906.log` inside the container still has it). The failed request `amzn1.echo-api.request.970b119f...` was the `AudioPlayer.PlaybackFailed` event (offset 1ms, MEDIA_ERROR_SERVICE_UNAVAILABLE, token = the Ribs episode). Its response was NOT produced by `PlaybackFailedEventHandler` (that handler already returns `BuildKeepAliveResponse`): the JF-477 session lookup exceeded its 2000ms fast-fail budget, and `BaseHandler.HandleRequestAsync`'s session-null degradation answered the EVENT with the `UserNotFound` Tell ("Utente non trovato..."), which Amazon rejected. The follow-up `System.ExceptionEncountered` (corr=e54b0532) was then itself answered with another illegal Tell ("Qualcosa è andato storto") by `ExceptionHandler`.

**Fixes (the whole bug class, not just the incident site):**

- `BaseHandler.IsEventRequest(request)` (public, shared): true for `AudioPlayerRequest`, `SessionEndedRequest`, `SystemExceptionRequest` (Amazon docs: those responses may not carry outputSpeech/card/reprompt).
- `BaseHandler.HandleRequestAsync`: all three user/session degradation sites now route through `BuildUserNotFoundResponse` -> keep-alive for event requests, Tell otherwise.
- `ExceptionHandler.HandleAsync`: keeps the classify+log, now returns `BuildKeepAliveResponse()` instead of a Tell.
- `AlexaSkillController`: the four Tell sites (user-null, unhandled request, timeout catch, exception catch; `req` hoisted so the catches see the parsed request) degrade through `DegradeForEventRequest`.
- `CircuitBreakerInterceptor`: an OPEN circuit no longer short-circuits event requests with the `ServerUnavailable` Tell (same class; found during review sweep). `AudioDeviceCapabilityInterceptor` already passes non-IntentRequests; `RequestPipeline`'s SkillWarmingUp catch is not event-reachable (the radio-track path queries genres directly, no warming-gated index) and was left alone.

### Tests (+30, full suite 3421 green; baseline 3391)

- `VideoAppStreamPolicyTests`: `AudioRequiresTranscode` matrix (eac3/ac3/truehd/dts/dtshd/dts-hd/EAC3 true; aac/mp3/flac/null/empty false).
- `YesIntentHandlerTests` (resume-yes wiring, via an `EpisodeWithStreams` GetMediaStreams seam): EAC3 episode -> `episode/{id}/audio.m3u8?start=` URL + directive offset 0 + no `/Audio/` URL; AAC episode -> raw static `/Audio/` URL + offset preserved.
- `VideoAudioControllerTests`: `BuildEpisodeAudioHlsFfmpegArguments` (seek-before-input, single `0:a:0` map, no `-c:v`, aac stereo 192k, 10s/append_list/mpegts, base URL with start path, no `-shortest`; from-zero has no `-ss`; aac source copies), `EstimateEpisodeAudioEncodeBytes` (96MB/h ceil shape), `EpisodeAudioCacheKey` distinctness, playlist 401/400, cache-miss flow (fake ffmpeg records audio-only args into the VARIANT directory, serves token-injected playlist), `GetEpisodeAudioSegment` (serves the variant dir, wrong-start 404, no-token 401).
- `EventHandlerTests`: ExceptionHandler returns keep-alive with no outputSpeech (replaces the old Tell assertion); `HandleRequestAsync` PlaybackFailed + session-miss -> no outputSpeech (the incident regression test); intent + session-miss still Tells; `IsEventRequest` classification; `CircuitBreakerInterceptor` open circuit passes events through and still short-circuits intents.

### Verification

- `dotnet build Jellyfin.Plugin.AlexaSkill.sln`: Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test Jellyfin.Plugin.AlexaSkill.Tests`: Passed! - Failed: 0, Passed: 3421, Skipped: 0, Total: 3421.
- /simplify: applied (test-harness extraction in EventHandlerTests). Skipped, deliberately: unifying `StreamHlsEpisodeAudioCore` with `StreamHlsEpisodeCore` (~150-line mirror) and parameterizing the flat-bytes-per-hour estimators; both match the file's existing per-family-Core convention and touching the device-verified JF-498 path for symmetry is not worth the risk here. If a reviewer wants the consolidation, it should be its own task (same shape as the JF-382 search-path consolidation).
- code-review: done in-session (task rules ban sub-agents), including the exhaustive outputSpeech-producer sweep that found the CircuitBreakerInterceptor site.
- DoD #6/#8 unchecked: no interaction-model or locale-string change happened (N/A). #7: handler-level tests stand in for SMAPI E2E (out of scope under this task's no-deploy/no-SMAPI rule); on-device verification of the actual Dot playback remains open, as does a deploy.

### Open items

- On-device verification (Dot resume of the Ribs episode) pending deploy; the segment-fetch behavior of AudioPlayer against the live playlist is the thing to watch in `podman logs`.
- `SkipForwardBackIntentHandler` during episode-audio playback (documented above).
- Position recording for start-shifted streams is stream-relative (documented above); if that matters for cross-client progress, the composite-token route (`StreamTokenCodec`) is the known extension point.
