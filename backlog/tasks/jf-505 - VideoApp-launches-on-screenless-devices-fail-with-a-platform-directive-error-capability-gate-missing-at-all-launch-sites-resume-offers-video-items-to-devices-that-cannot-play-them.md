---
id: JF-505
title: >-
  VideoApp launches on screenless devices fail with a platform directive error:
  capability gate missing at all launch sites + resume offers video items to
  devices that cannot play them
status: Done
assignee: []
created_date: '2026-09-06 15:14'
updated_date: '2026-09-06 16:52'
labels:
  - video
  - device-capabilities
  - bug
dependencies: []
references:
  - corr=58826ad3
  - corr=f0240020
  - JF-498
  - Alexa/Interface/
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the 2026-09-06 Dot (screenless Echo) session: every video launch on a device without the VideoApp interface fails with the platform error 'il dispositivo di destinazione non supporta la direttiva specificata'. Two evidenced paths: PlayNextEpisodeIntent direct (17:09:17 corr=58826ad3: session hit, VideoApp routing ran, directive sent, platform rejected) and the accepted resume offer (17:12:00 corr=f0240020: 'Yes: confirming resume' for The Bear episode 'Ribs' launched VideoApp on the Dot). The plugin has an interface-capability layer (Alexa/Interface/, used for APL) but NO VideoApp gate at any of the 11 launch sites wired by JF-498 nor the Yes-resume path.

FIX: a shared capability check (context.System.Device.SupportedInterfaces contains VideoApp) at the GetVideoAppLaunchUrl choke point's callers (or a wrapper around the launch-response builders): without the interface, respond with a NEW localized string ('questo contenuto richiede un dispositivo con schermo' + 17 locales) instead of the directive. ALSO the resume-offer builder (LaunchResume) must not offer a VIDEO item on a screenless device: either skip the offer, or fall back to the user's last AUDIO item (the last-played ledger is per-user: it offered a Show-played episode to the Dot); the cross-device video offer can never be honored there. Unit tests: VideoApp interface present/absent at the choke point + the launch builders; resume-offer on screenless with last-played video vs audio. Device verification on the Dot.
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

## Implementation Notes (2026-09-06, first pass; status stays In Progress)

### Gate design (chosen: shared launch-response builders)

`Alexa/Interface/VideoAppCapabilities.cs` (new) holds the request-side detector
`DeviceSupportsVideoApp(Context?)`: TRUE when `context.System.Device.SupportedInterfaces`
is null (fail-open, see below) or contains the `VideoApp` key (the REQUEST-context key;
distinct from the manifest's `VIDEO_APP` interface name). Single import point, mirrors
`AplHelper.DeviceSupportsApl` in shape but lives next to `VideoAppInterface` in
`Alexa/Interface/`.

Three response-construction chokepoints now own the gate, one per response family:

1. `BaseHandler.BuildVideoAppLaunchResponse(context, locale, sourceUrl, title, outputSpeech)`
   (new, protected): the canonical VideoApp.Launch response for VIDEO content
   (Movie/Episode/live TV). Without the interface it emits NO directive and returns the
   localized `VideoRequiresScreen` Tell (`shouldEndSession=true`). Every one of the 12
   former inline `new SkillResponse { new VideoAppLaunchDirective ... }` blocks in the
   handlers was replaced by a single call to this builder, so the gate is written once
   and future launch sites inherit it by convention (a grep for
   `new .*VideoAppLaunchDirective` outside BaseHandler now returns nothing).
2. `BaseHandler.BuildVideoAppAudioResponse` (gained a `Context? context = null` param):
   audio content via video-audio (NativeControlsForAudio/Books). A screenless device
   cannot honor the directive, but the CONTENT is audio it can play, so the builder
   DEGRADES to the plain `BuildAudioPlayerResponse` play instead of refusing (a
   "requires screen" Tell for a song would be wrong UX).
3. `BaseHandler.BuildAudiobookResumeResponse` (gained `Entities.User user` +
   `Context? context` params): same degradation, to the AudioPlayer RESUME with
   `offsetMs = startTicks` converted (AudioPlayer honors offsets; VideoApp cannot).

Recursion safety: `BuildAudioPlayerResponse`'s native-controls delegation (the only
caller of builder 2 reachable from inside the audio path) now also requires
`DeviceSupportsVideoApp(context)`, so builder 2's fallback can never re-enter the
video-audio branch. Both sides gate on the same single helper, so they cannot disagree.

Fail-open rationale: a context whose `SupportedInterfaces` map is absent entirely keeps
the pre-gate launch behavior. Real Echo requests always report the map (the Dot reports
interfaces WITHOUT VideoApp, which is the gated shape); absent data means we cannot
know, and refusing video on a guess would regress devices that do report capability
data differently. This also keeps every pre-existing unit test context (no
SupportedInterfaces) on the launch path instead of silently flipping to the Tell.

### Launch sites covered (all former directive constructions)

Through `BuildVideoAppLaunchResponse`:
- `Handler/Intent/PlayVideoIntentHandler.cs` (HandleAsync launch block)
- `Handler/Intent/PlayEpisodeIntentHandler.cs` (explicit episode launch)
- `Handler/BaseHandler.cs` `PlayNextUpEpisodeAsync` (shared next-up core, used by
  PlayEpisodeIntentHandler + PlayNextEpisodeIntentHandler; gained a `Context` param)
- `Handler/Intent/SearchMediaIntentHandler.cs` (`PlayItem` video branch)
- `Handler/Intent/PlayRandomIntentHandler.cs` (movie/episode first item)
- `Handler/Intent/ResumeIntentHandler.cs` (server-side-progress video resume)
- `Handler/Intent/RecommendIntentHandler.cs` (movie recommendation)
- `Handler/Intent/StartOverIntentHandler.cs` (restart video)
- `Handler/Intent/ContinueWatchingIntentHandler.cs` (video resume)
- `Handler/Intent/AplUserEventHandler.cs` (`HandleSelectItem` movie tap)
- `Handler/Intent/YesIntentHandler.cs` `PlayVideo` (disambiguation-confirmed video;
  gained a `Context` param) - the Yes-resume VIDEO path
- `BaseHandler.BuildChannelLaunchResponseAsync` (live TV/radio channel tier)

Through the degrading audio builders:
- `BaseHandler.BuildAudioPlayerResponse` native-controls delegation (music/audiobooks
  under NativeControlsForAudio/Books) - the Yes-resume evidence path (corr=f0240020)
- `Handler/Intent/PlayBookIntentHandler.cs` (audiobook fresh start + resume)
- `Handler/Intent/YesIntentHandler.cs` (audiobook resume confirm + book disambiguation)

### Resume offer (screenless)

`LaunchRequestHandler.BuildResumeOfferResponse` (the shared bottom of BOTH offer
sources: the AudioPlayer-token offer and the device-ledger offer) now declines a
Movie/Episode item when the device lacks VideoApp and calls
`BuildScreenlessAudioFallbackOffer`: it queries the per-user last-played ledger via the
existing `FindLastPlayedItemWithProgress` helper restricted to content-access-filtered
Audio/AudioBook kinds, and offers the most recent audio item WITH PROGRESS; when none
exists it returns null and the launch falls straight to the welcome response.
`HandleResumeOfferAsync` maps a declined offer to the welcome response. A screen-capable
device keeps the video offer unchanged. Note the gate also stops the incident's
recording loop at the source: a gated launch emits no directive, so
`LastPlayedResponseInterceptor` no longer records a rejected episode as the device's
last-played item (persisted pre-fix entries are still handled by the substitution).

### Locale string

`VideoRequiresScreen` added to all 17 locale JSONs (inserted next to
`AudioPlayerNotSupported`). it-IT: "Mi dispiace, questo contenuto richiede un
dispositivo con schermo. Prova a chiedere da un Echo Show o Fire TV." (contains the
task's core phrase verbatim). de/es/fr follow those files' ASCII-safe convention.

### Verification (2026-09-06)

- `dotnet build` (Debug + Release): 0 errors, 0 warnings.
- `dotnet test Jellyfin.Plugin.AlexaSkill.Tests`: 3377 passed / 0 failed
  (baseline 3345 + 32 new: 9 gate, 4 resume-offer, 18 locale, 1 extra theory case).
- `python3 scripts/validate_locales.py`: PASS.
- New tests: `Unit/VideoAppCapabilityGateTests` (chokepoint present/absent/fail-open,
  PlayVideo + PlayChannel handler sites, both audio builders degrade, native-controls
  screenless keeps AudioPlayer), `Handler/ScreenlessResumeOfferTests` (video last-played
  -> audio fallback; -> no candidate = welcome; audio last-played offered as-is;
  screen-capable keeps video offer), `Unit/LocaleStringsTests` additions (17/17 key
  resolution + it-IT wording).

### Known scope notes

- Handlers still set `session.NowPlayingQueue`/`FullNowPlayingItem` before the gate
  fires (same as the pre-fix rejected-directive path); a gated launch can leave a
  now-playing record for content that never played. Same-as-today, not worsened.
- On a gated launch the caller's `GetVideoAppLaunchUrl(...)` argument is still evaluated
  (one codec-probe + URL mint wasted); judged cheaper than a `Func<string>` indirection
  across all 12 call sites (simplify pass, skipped deliberately).
- /simplify ran (4-angle pass applied inline: shared test context helpers hoisted to
  TestHelpers, dead `subtitle` param removed, private test doubles replaced with
  SharedGateProbeHandler). The /code-review high gate, E2E/simulate-skill, and device
  verification on the Dot are still pending; subagent-dispatching review skills could
  not run in this worker's no-subagent session, so the external review remains to be
  executed by the coordinator before any Done transition.

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Orchestrator /simplify pass dispositions (2026-09-06): APPLIED the gate-first reorder in BuildChannelLaunchResponseAsync (capability check before the 5s resolver round-trip; the last-played record is unconditional-for-capable again since the refusal path cannot reach it); the shared IsVideoAppLaunchItem predicate (Movie/Episode/LiveTvChannel) replacing the drifted hand-written type lists at the resume-offer gate and the ResumeIntent router (ResumeIntent now correctly includes LiveTvChannel); the DELIBERATE comment pinning the screenless audiobook fallback to plain UserData ticks (no tracker/playlist on a plain AudioPlayer resume); the test-only/fail-open doc on the context-less BuildAudioPlayerResponse overload. SKIPPED (documented): the ~41-call-site removal of the dead overload (test churn), the assertion-triple and config-boilerplate consolidation in VideoAppCapabilityGateTests, the factory composition in ScreenlessResumeOfferTests.CreateContextWithAudioToken (all test-file polish, no production impact), and the double ResolveJellyfinUser in the screenless path (in-memory lookup, negligible).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented, reviewed, deployed 2026-09-06 (commit 24a885e0, DLL e1ae26e6). The VideoApp capability gate lives in the shared launch builders: BuildVideoAppLaunchResponse refuses with the localized VideoRequiresScreen Tell (17 locales) on devices without the VideoApp interface; the audio-capable builders degrade to plain AudioPlayer (a song needs no screen); the resume offer never proposes Movie/Episode/LiveTvChannel to a screenless device and falls back to the last audio item with progress; gated launches no longer record as device last-played; the channel launch checks capability BEFORE the 5s stream resolver. Detector fails open only on an entirely absent SupportedInterfaces map (real Echoes always report it). Formal review's two Important findings applied (five context-less audio sites threaded; channel record + LiveTvChannel gate) and the simplify pass landed the shared IsVideoAppLaunchItem predicate (fixing the ResumeIntent drift). Device verification pending on the Dot: video requests should now speak 'questo contenuto richiede un dispositivo con schermo' instead of the platform directive error, and the launch-time resume offer should name an audio item.
<!-- SECTION:FINAL_SUMMARY:END -->
