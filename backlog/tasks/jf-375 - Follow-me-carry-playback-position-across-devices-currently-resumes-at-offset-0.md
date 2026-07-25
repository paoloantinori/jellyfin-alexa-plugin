---
id: JF-375
title: >-
  Follow-me: carry playback position across devices (currently resumes at offset
  0)
status: To Do
assignee: []
created_date: '2026-07-25 14:34'
labels:
  - enhancement
  - follow-me
  - playback
  - multi-device
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/FollowMeIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/FollowMeIntentHandlerTests.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-me transfer currently resumes the current track from offset 0 (the FollowMe_ResumesAtOffsetZero_ByDesign unit test locks this; verified on-device 2026-07-25). This is a plugin-side limitation that COULD be lifted: track the source device's elapsed playback position and apply it as the AudioPlayer.Play OffsetInMilliseconds on the target device.

This is a real feature, not a one-line fix. It requires:
1. Capturing per-device playback offset. DeviceQueueManager (Alexa/Playback/DeviceQueueManager.cs) tracks per-ITEM resume position (ItemPositionState) for same-device resume, but has no cross-device transfer offset field. The source device's elapsed position must be recorded from AudioPlayer events (PlaybackStarted/PlaybackNearlyFinished/PlaybackStopped carry offsetInMilliseconds) keyed by device, and read by FollowMeIntentHandler at transfer time.
2. Applying it: FollowMeIntentHandler.cs:136 builds the stream URL with no offset; BuildAudioPlayerResponse would need the offset passed through (it already accepts an offsetInMilliseconds param, defaulting to 0).
3. Revisiting the FollowMeSuccess locale strings (all 17), which on 2026-07-25 were deliberately rewritten to NOT promise position-keeping ('Continuing X on this device'). Once true resume ships, those strings should reflect it.

KNOWN COMPLICATION (from CLAUDE.md): Jellyfin's PlaybackStopped event clears FullNowPlayingItem before the resume request arrives, so the offset must come from AudioPlayer.Token or the DeviceQueue, not FullNowPlayingItem.

OUT OF SCOPE for this task: the source-device-does-not-stop limitation. That is a platform wall (custom Alexa skills cannot send directives to a device other than the requester), not fixable plugin-side. Documented in README.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Design: decide where the per-device playback offset is stored. DeviceQueueManager currently tracks per-item resume position (ItemPositionState) but NOT a cross-device transfer offset. The source device's elapsed offset must be captured at transfer time (from PlaybackStopped/PlaybackNearlyFinished events on the source) and read by FollowMeIntentHandler on the target
- [ ] #2 FollowMeIntentHandler builds the stream URL with the captured offset (AudioPlayer.Play OffsetInMilliseconds) instead of 0. Update the FollowMe_ResumesAtOffsetZero_ByDesign unit test: it currently locks offset-0; flip it to assert the captured offset is applied (or split into two tests)
- [ ] #3 Update the FollowMeSuccess locale strings (all 17) to reflect that position IS now carried, once the feature lands. The current strings (set 2026-07-25) say 'Continuing X on this device' and deliberately do NOT promise position-keeping; they must be revisited when this ships
- [ ] #4 Live verification on 2 Echos: source plays to a mid-point position, follow-me transfer, target resumes from (approximately) that position, not 0
- [ ] #5 Investigate the timing gap: Jellyfin's PlaybackStopped event clears FullNowPlayingItem before the resume request arrives (per CLAUDE.md). Confirm the offset source survives this (AudioPlayer.Token or the DeviceQueue offset, not FullNowPlayingItem)
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
