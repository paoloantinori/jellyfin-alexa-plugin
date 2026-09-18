---
id: JF-568
title: >-
  ResolvePlayingMedium: video-kind ledger item + queue-advanced token after an
  audio-transcode video launch misclassifies Audio as Video
status: In Progress
assignee: []
created_date: '2026-09-15 09:29'
labels:
  - ledger
  - transport
  - edge-case
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review P3 (JF-564, 2026-09-15): a video-kind item (Movie/Episode) launched on the audio-only transcode path (JF-507, screenless or EAC3 audio) puts the movie in the device last-played ledger AND the token; a subsequent PlaybackNearlyFinished enqueue (PostPlayBehavior=AutoPlay radio) moves the token to the radio track WITHOUT recording. BaseHandler.ResolvePlayingMedium (and RepeatIntentHandler's identical videoDisplacedAudio rule) then reads token != ledger + video-kind ledger item as displacement, classifies Video, and Next/Previous answer the "can't navigate video by voice" line while audio is genuinely playing. Narrow window (screenless + episode audio + AutoPlay + transport intent). The trade-off is byte-for-byte the one the RepeatIntentHandler precedent carries (JF-562), so this is consistent intended semantics, not a new bug; file tracks the ledger-design fix (e.g. record enqueued directives too, or persist the launch route per item).
<!-- SECTION:DESCRIPTION:END -->
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

### Chosen shape (the task's option a, extended DeviceQueue)

`DeviceQueue.LastPlayedLaunchRoute` (string?, values `"Audio"`/`"VideoApp"` as the
enum NAME, the `RepeatMode` string-for-JSON-compat shape) persisted in the queue
JSON beside `LastPlayedItemId`. `DeviceQueueManager.LaunchRoute` is a nested
public enum (the `QueueInsertPlacement` precedent). No second ledger: the writes
ride the SAME `RecordLastPlayed` chokepoints that write the item id today.

- `RecordLastPlayed(deviceId, itemId, LaunchRoute launchRoute)` - the route is a
  REQUIRED parameter, so a future recording site cannot compile while silently
  recording no route. The unchanged-item short-circuit now compares item AND
  route: the same item re-launched on the other route (the
  BuildAudioPlayerResponse native-controls delegation that re-records inside
  BuildVideoAppAudioResponse; a screenless-degrade play followed by a VideoApp
  play of the same item) must FLIP the route, not short-circuit on the item id.
- `GetLastPlayedLaunchRoute(deviceId)` - read-side counterpart returning
  `LaunchRoute?`; null on a pre-JF-568 queue file, an unknown device, or a route
  name a newer plugin wrote (strict `Enum.TryParse`; unknown names degrade to
  null, i.e. legacy semantics, never throw).

### Recording sites wired (all 5 `RecordLastPlayed` production callers)

- `PlaybackLaunchBuilder.BuildAudioPlayerResponse` (the audio chokepoint):
  **Audio**. This is the JF-507 audio-only-transcode and JF-589 audio-route
  episode path, the exact incident chain. The native-controls delegation below
  the record re-records with VideoApp when it actually builds a VideoApp.Launch,
  so the stored route always names the directive that went out.
- `LastPlayedResponseInterceptor` (movie/episode VideoApp directives): **VideoApp**.
- `PlaybackLaunchBuilder.BuildChannelLaunchResponseAsync` (live TV): **VideoApp**.
- `PlaybackLaunchBuilder.BuildAudiobookResumeResponse` (VideoApp book resume):
  **VideoApp** (its screenless degrade goes through BuildAudioPlayerResponse, so
  it records Audio, correctly).
- `PlaybackLaunchBuilder.BuildVideoAppAudioResponse` (audio-via-VideoApp,
  audiobook concat): **VideoApp**.

### Classification rule change

`ResolvePlayingMedium` gains ONE arm, right after the token-ownership arm and
before the item resolve: a ledger entry recorded on the Audio route returns
`PlayingMedium.Audio` whatever the item kind (and skips the DB resolve, mirroring
the token arm). Only VideoApp-routed or legacy null-routed entries fall through
to the kind-based rules. Classification is now route-driven for recorded
launches, kind-driven only for legacy/absent routes. Consequence beyond the
incident: a flat-audio book (NativeControlsForBooks off) with a moved token now
classifies Audio instead of VideoAppAudiobook, matching what the token arm
already did for a matching token.

`RepeatIntentHandler.videoDisplacedAudio` (the twin) adds
`recordedRoute != LaunchRoute.Audio` to the conjunction: route Audio means the
audio pipeline launched the ledger item, so the moved token is queue advance and
the token's item (the radio track) repeats.

### Backward / forward compatibility

- Old file, new plugin: `LastPlayedLaunchRoute` missing deserializes to null;
  readers fall through to the kind rules, so the legacy classification is
  preserved byte-for-byte (pinned by test).
- New file, old plugin: System.Text.Json skips unknown members by default
  (unmapped-member handling is never set to Disallow in `JsonOptions`), the same
  additive-field mechanism the queue file already rode for ItemPositionState,
  ActiveLaunchBaseMs and PendingLaunchBaseMs.
- Route values are written as the enum NAME, so a future route value from a
  newer plugin degrades to null (legacy semantics) in this one, never throws.

### Tests (red-first)

Red before the rule change, green after, both TFMs (the plumbing was landed
first with NO behavior change so the red set isolates the rule):

- `PlaybackLaunchBuilderMediumTests.Medium_LedgerMovieAudioRoute_TokenMovedToRadioTrack_YieldsAudio`
  (the incident chain; RED: got "Video")
- `PlaybackLaunchBuilderMediumTests.Medium_LedgerEpisodeAudioRoute_WithoutToken_YieldsAudio`
  (the JF-589 shape, no-token variant; RED: got "Video")
- `PlaybackLaunchBuilderMediumTests.Medium_LedgerAudioBookAudioRoute_TokenMoved_YieldsAudio`
  (flat-audio book; RED: got "VideoAppAudiobook")
- `RepeatIntentHandlerTests.HandleAsync_AudioRouteVideoKindLedger_TokenMoved_DoesNotDisplace`
  (RED: cannot-repeat Tell instead of restarting the radio track)
- Pins (green before AND after): `Medium_LedgerMovieVideoAppRoute_TokenMoved_YieldsVideo`
  (displacement as today), `Medium_LegacyQueueFileWithoutRoute_KeepsKindBasedClassification`
  (hand-written pre-JF-568 queue JSON loaded through the ctor disk path, the
  upgrade scenario), `RecordLastPlayed_RouteRoundTripsThroughDisk`,
  `RecordLastPlayed_SameItemDifferentRoute_UpdatesRoute`,
  `GetLastPlayedLaunchRoute_LegacyFileWithoutRoute_Null`,
  `GetLastPlayedLaunchRoute_UnknownRouteName_Null`.

### Verification

- `dotnet test Jellyfin.Plugin.AlexaSkill.Tests` (no --no-build): 4094/4094
  passed on BOTH net9.0 and net10.0, 0 failed.
- `dotnet build Jellyfin.Plugin.AlexaSkill.sln -c Release -warnaserror`:
  0 warnings, 0 errors.
- No interaction-model, locale-string, or response-shape changes (DoD items 6, 8
  not applicable; nothing user-facing moved).

### Risks

- The route is only as truthful as its recording site; a FUTURE launch path that
  bypasses all five chokepoints would record nothing (null route = legacy
  behavior), never a wrong-route entry. The required parameter keeps new sites
  honest at compile time.
- The delegation path (BuildAudioPlayerResponse records Audio, then
  BuildVideoAppAudioResponse re-records VideoApp) briefly holds route Audio
  in-memory before the second write lands on the same request thread; no reader
  runs between them (both writes happen inside the one response build).
