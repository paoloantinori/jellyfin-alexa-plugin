---
id: JF-637
title: >-
  JF-636 follow-ups: consolidate the variant-HLS machinery, the JF-632 gate
  preamble, and the slot-resolution walk
status: To Do
assignee: []
created_date: '2026-09-26 14:48'
labels:
  - tech-debt
  - refactor
  - consolidation
dependencies: []
references:
  - JF-636
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-636 (/simplify, three parallel reviewers) review round. The feature shipped correct; these are the deliberate NOT-NOW consolidations the reviewers flagged as repeat-offender shapes. All in Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs unless noted.

1. Variant-HLS core extraction (BLOCKING-class from the quality agent, consciously skipped): StreamHlsEpisodeAudioCore and StreamHlsAudioSpeedCore are near-identical ~120-line lock/fast-path/debris-cleanup/first-segment-wait/gate/monitor blocks (with StreamHlsVideoAudioCore and the audiobook core as older, looser siblings). Historically bug-prone invariants (JF-499 W3 race guard, JF-498 debris cleanup, CA2025 monitor boundary, double try/catch flag cleanup) had to be fixed copy by copy. Extract one shared ServeVariantHlsAsync(cacheKey, artModifiedTicks, validation, ffmpegArgs, estimateBytes, label, encodeFlags) parameterized over the ~4 real differences. Also: the `if (Guid.TryParse(...))` guard around token validation is always-true for the speed route (copied shape), and the speed path rides the "Episode"-named helpers/_activeEpisodeEncodes registry (rename or doc as variant-generic).

2. First-segment-wait helper: the 200x100ms poll + kill/dispose/TryRemove catch pair is now in its 4th inline copy (remux, song, episode-audio, audio-speed). WaitForFirstSegmentOrKillAsync(process, hlsDir, playlistPath, cacheKey, label) collapses them; do it together with item 1.

3. JF-632 gate preamble (SetPlaybackSpeedIntentHandler.cs:~124 vs SleepTimerIntentHandler.cs:~140): the ledger-snapshot + IsVideoAppMedium/ledgerVideoRouted gate is now copied verbatim twice. Extract one shared helper (BaseHandler or DeviceQueueManager) returning the gate evidence so the third transport intent cannot drift.

4. ER_SUCCESS_MATCH authority walk: third private copy (EpisodePosition.IsLatest, BrowseLibraryIntentHandler.GetCanonicalSlotValue, PlaybackSpeed.Resolve). A shared SlotResolution.FirstMatch(Slot) enumerator would own it; the per-slot-resolver convention is deliberate, so decide at the fourth copy.

5. DeviceQueue four-map lockstep (Alexa/Playback/DeviceQueue.cs): the base/rate pairing across Active/Pending maps is comment-enforced at 5 sites; a private paired-write helper would harden the "pending rate exists iff pending base exists" invariant (the RecordLaunchBase short-circuit checks only PendingLaunchBaseMs).

6. Torn launch-scope reads outside the event path: GetActiveLaunchScope(deviceId,itemId) exists since JF-636 and ComposeEventPositionTicks/ResolveResumedAudioLaunch use it; any future reader needing both base and rate must use it too (two individual reads can straddle a concurrent RecordLaunchBase).

Done as part of JF-636 instead (not to redo): the 0.75x JF-521 clamp inversion, superseded-encode killing (_activeAudioSpeedEncodeProcesses + KillSupersededSpeedEncodes), the audiobook honest refusal (CannotChangeSpeedForBook), and the AppendEventAudioHlsTail shared ffmpeg tail.
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
