---
id: JF-586
title: >-
  Episode plays on screenless devices (Echo Dot/simulator): the episode launch
  family degrades to the AudioPlayer audio-only route instead of the
  screen-required refusal
status: Done
assignee: []
created_date: '2026-09-17 19:02'
updated_date: '2026-09-17 20:01'
labels:
  - episodes
  - screenless
  - audio-fallback
  - podcast
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
User request 2026-09-17 (live, simulator-verified then confirmed by code reading): 'riproduci l'ultimo episodio di morning' on a SCREENLESS device (Echo Dot, and the Alexa web simulator which is screenless) answers 'Mi dispiace, questo contenuto richiede un dispositivo con schermo' - the JF-505 gate in the generic VideoApp launch refuses, because TvNextUpService.LaunchEpisodeAsync (JF-583) and PlayEpisodeIntentHandler launch episodes via BuildVideoAppLaunchResponse(Async) which hard-refuses without a VideoApp-capable device. The user's primary use case IS podcasts on Dots (Morning on IlPost .strm episodes). ALL the building blocks already exist in-tree: ResolveAudioLaunchSource(item, itemId, user, offsetMs) handles Movie/Episode (EAC3-family audio -> the audio-only episode HLS transcode with startTicks; decodable audio -> the static /Audio/{id}/stream), and BuildVideoAppAudioResponse already degrades screenless->AudioPlayer for the native-controls-audio family (the JF-505 recursion-break precedent). SCOPE: the episode launch family (TvNextUpService.LaunchEpisodeAsync for both next and latest cores; PlayEpisodeIntentHandler's episode launch; check StartOver/Resume episode arms for the same refusal) degrades to the AudioPlayer launch on a screenless device instead of refusing - use ResolveAudioLaunchSource with the already-resolved resume offset (the transcode route takes startTicks; the static route takes offsetMs), keep the existing announce wording (NowPlayingWithPosition/PlayingLatest/PlayingNext families), keep the queue/now-playing seeding consistent with the audio chokepoint, and keep the VideoApp path byte-identical on capable devices. Also keep the episode auto-advance coherent (PlaybackNearlyFinished's JF-324 episode advance fires on the AudioPlayer path, so a Dot playlist of episodes continues naturally - verify the queue seeded by the audio route feeds it). Related: JF-583 (the position slot that motivated the test), JF-505 (the screenless-gate family), JF-507 (the codec gate).
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Builder shape (the one new API).** `PlaybackLaunchBuilder.BuildEpisodeLaunchResponseAsync(context, request, locale, item, user, sourceUrl, resumeTicks, outputSpeech)`, placed beside the VideoApp launch family. Contract: `item is Episode && !DeviceSupportsVideoApp(context)` degrades; EVERY other combination (any item on a capable device, a non-Episode on a screenless device) is a pure pass-through to the existing `BuildVideoAppLaunchResponseAsync`, so the capable path is byte-identical by construction and movies keep the capability refusal (JF-586 scope). The degrade rides `ResolveAudioLaunchSource` (JF-507): eac3-family episodes get the audio-only episode HLS transcode with resume minted as `?start=` (directive offset 0, launch base recorded at the chokepoint per JF-522); decodable episodes keep the static `/Audio/{id}/stream` with the resume offset on the DIRECTIVE (AudioPlayer can seek a static stream; the VideoApp Static route cannot). The stored position is clamped by the JF-565 rule (position >= runtime, or UNKNOWN runtime, the zero-tick .strm shape -> fresh start), so the degrade never mints an offset the stream cannot serve. The caller-chosen announce rides the FINAL response on the degrade (AudioPlayer playback does not steal the TTS channel the way a fast-start VideoApp player does, the JF-501 observation); on the capable route the announce stays progressive inside the shared builder exactly as today. `ShouldEndSession=true` comes from `BuildAudioPlayerResponse` (the JF-299 play rule).

**Per-arm adoption table.**

| Launch arm | Verdict | Notes |
|---|---|---|
| TvNextUpService.LaunchEpisodeAsync (shared tail; covers NextUp core, latest fallback, and the JF-583 recency core, i.e. PlayNextEpisode + PlayEpisode series-only) | ADOPTED | The user's primary ask ('riproduci l'ultimo episodio di morning'). Passes the already-resolved `resumeTicks`; session queue seeding (NowPlayingQueue + FullNowPlayingItem) runs BEFORE the launch call so it applies to both routes. |
| PlayEpisodeIntentHandler explicit season/episode arm | ADOPTED | `resumeTicks: 0` per the JF-565 fresh-play pin (explicit numbered ask is a relaunch-from-scratch). |
| StartOverIntentHandler Movie-or-Episode branch | ADOPTED | `resumeTicks: 0` (progress was just cleared above the launch). Movies pass through to the refusal inside the builder. |
| ResumeIntentHandler fallback-4 (server progress) IsVideoAppLaunchItem branch | ADOPTED | Passes `resumeTicks`; the caller's `claimPosition` gate is unchanged (position wording only when the VideoApp route delivered, and the transcode degrade delivers the same `?start=`; a static-route episode resumes with fresh wording, an understatement, never a false claim). LiveTvChannel/Movie keep the refusal via the pass-through. |
| ContinueWatchingIntentHandler episode arm | ADOPTED | Same shape as ResumeIntent fallback-4. |
| PlayVideoIntentHandler / SearchMediaIntentHandler.PlayItem / YesIntentHandler.PlayVideo / PlayRandomIntentHandler | LEFT (filed JF-587) | Episode-reachable generic video-search arms, outside this task's named family; the same refusal still fires there for episodes. Tracked as JF-587 (mechanical one-line swaps). |
| RecommendIntentHandler / AplUserEventHandler | NOT episode arms | Recommend queries Audio+Movie only (episodes never reach its VideoApp branch); APL carousel taps are Movie-only and require a screen by definition. |
| Movies anywhere | OUT OF SCOPE (kept refusing) | Pre-existing behavior per task scope; the builder's Episode-only test honors it (pinned by `BuildEpisodeLaunchResponse_ScreenlessMovie_KeepsVideoRequiresScreenTell`). |

**Queue / now-playing coherence (JF-324).** The screenless route goes through `BuildAudioPlayerResponse`, which owns the device last-played ledger record (`RecordLastPlayed`) and the JF-522 launch-base capture; `ResolvePlayingMedium` then classifies the playing episode as `PlayingMedium.Audio` (token == ledger item), so pause/resume transport routing on the Dot is the honest audio shape. The session queue seeding in `LaunchEpisodeAsync` happens before the launch build, so it is identical on both routes; the episode auto-advance (`PlaybackNearlyFinishedEventHandler.TryAutoAdvanceNextEpisodeAsync`, JF-324) resolves the next episode from the finishing item's AudioPlayer token + series (never from the seeded session queue directly) and its enqueue rides `ResolveAudioLaunchSource` itself, so a Dot binge advances naturally when PostPlayBehavior=AutoPlay (VideoApp playback emits no events at all, so this advance possibility is NEW on the Dot, not a regression risk).

**Tests (TDD red-first: the 9 behavioral degrade tests failed against a pass-through stub, then went green with the implementation).** New tests: 4 in TvNextUpServiceTests (latest-episode transcode+resume, static fresh, static in-range resume offset on the directive, stale-position clamp), 1 in PlayNextEpisodeIntentHandlerTests, 1 in PlayEpisodeIntentHandlerTests (transcode fresh, no start=), 1 in StartOverIntentHandlerTests, 1 in ContinueWatchingIntentHandlerTests (transcode+start), 1 in ResumeIntentHandlerServerProgressTests (static+offset), plus 3 chokepoint tests in VideoAppCapabilityGateTests (screenless episode transcode, screenless movie keeps the refusal, capable episode keeps the VideoApp directive). Existing capable-route pins (the JF-565 `?start=` tests, the JF-501 progressive-announce tests) now exercise the new builder's pass-through and pass unchanged: that IS the byte-identity lock. Full suite: 4063/4063 green on net9.0 AND net10.0; `dotnet build -c Release` 0 warnings. No interaction-model, locale-string, or session-attribute changes (DoD #4/#6/#8 not applicable; the announce wording reuses existing keys).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DONE (commit 68d6d1b7). Episode launches now degrade to the audio-only AudioPlayer route on screenless devices instead of the screen-required refusal: the user's case ('riproduci l'ultimo episodio di morning' on an Echo Dot or the web simulator, live-verified refusal 2026-09-17) plays audio. SHAPE: PlaybackLaunchBuilder.BuildEpisodeLaunchResponseAsync, one predicate (Episode + !DeviceSupportsVideoApp degrades; every other combination a pure pass-through to BuildVideoAppLaunchResponseAsync) so the capable-device path is byte-identical BY CONSTRUCTION (the pre-existing JF-565 ?start= and JF-501 progressive-announce pins route through the pass-through unchanged) and movies keep the refusal by the same construction (pinned by test). The degrade rides ResolveAudioLaunchSource (JF-507): eac3-family audio -> the audio-only episode HLS transcode with the resume as ?start= and directive offset 0 (the JF-522 launch base recorded at the BuildAudioPlayerResponse chokepoint); decodable audio -> the static /Audio stream with the resume offset ON THE DIRECTIVE (AudioPlayer can seek a static stream, unlike the VideoApp Static route); the JF-565 fail-closed clamp now defined ONCE as ClampResumeTicksToRuntime, shared with the VideoApp slice route (/simplify R1: the two copies fed the announce gate and the delivered offset respectively and could drift). ADOPTED ARMS: TvNextUpService.LaunchEpisodeAsync (the shared tail: NextUp core, latest fallback, the JF-583 recency core), PlayEpisode explicit S/E (resumeTicks 0 per the fresh-play pin), StartOver episode branch (fresh), ResumeIntent fallback-4 episodes, ContinueWatching episodes. RESIDUAL: the four episode-reachable arms outside the named family (PlayVideo, SearchMedia.PlayItem, YesIntent.PlayVideo, PlayRandom) still refuse, filed as JF-587 same-turn; Recommend and APL taps verified not episode arms. GATES: /simplify 4-angle pass (R1 shared clamp applied, A1 doc pointer on the routing helper applied, S1 redundant assertion trim applied, E1 double media-streams probe accepted as-is with the JF-501 ordering rationale, the announce-understatement kept as the documented safe-direction choice); code-review high via feature-dev:code-reviewer: NO blockers or majors - the KEY launch-base risk verified correct end to end (transcode: base = resumeMs at the chokepoint with directive 0; static: base 0 with offset on the directive; ComposeEventPositionTicks composes item-absolute in both arms), the JF-324 auto-advance verified coherent on the Dot (seeding precedes the launch; the advance resolves from the AudioPlayer token + series and enqueues via ResolveAudioLaunchSource), both clamp copies verified unable to disagree, the mixed-probe fail-open case verified never producing a false announce claim; three minors noted (the deliberate understatement, the bounded double probe, test-fixture honesty nit). Tests: 12 new red-first (transcode+resume, static fresh, static offset-on-directive, the stale clamp, per-arm handler degrades, the movie-refusal and capable-route pins). Suite 4063/4063 both TFMs, Release 0 warnings. On-device/simulator verification: the user can retest 'riproduci l'ultimo episodio di morning' in the web simulator (screenless) after this deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
