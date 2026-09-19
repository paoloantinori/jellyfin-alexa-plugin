---
id: JF-375
title: >-
  Follow-me: carry playback position across devices (currently resumes at offset
  0)
status: Done
assignee: []
created_date: '2026-07-25 14:34'
updated_date: '2026-09-19 08:29'
labels:
  - enhancement
  - follow-me
  - playback
  - multi-device
dependencies: []
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped and deployed (a034ca4f). Design (AC#1): the offset source is the plugin-owned per-device state, freshest first - the source queue's live pointer (CurrentItemId/CurrentPositionTicks, written by the PlaybackStopped AND PlaybackNearlyFinished event paths) then the durable per-item store (ItemPositionState, GetStoredPositionTicks); both survive the FullNowPlayingItem clearing (AC#5 confirmed: neither reads session state) and both are read BEFORE the source-queue Clear. Applied (AC#2): through BuildAudioPlayerResponse's offsetInMilliseconds (the same mechanism Resume/SleepTimer use on static streams), run through the ONE fail-closed runtime clamp (ClampResumeTicksToRuntime widened to internal - unknown runtime or beyond-runtime drops to 0 AND logs, replacing my first draft's silent fail-open third copy). Announcement (AC#3): FollowMeSuccessResume (17 locales, 'right where you left it' wording, speechcon + break shape mirroring the existing key) only when an offset actually carries; plain FollowMeSuccess at 0 - honest in both directions. README feature + FAQ updated. REVIEW TRAIL: the combined simplify+code-review dispatch caught a REAL production bug in my first draft (C1): the pointer compare string-matched 'N' GUID format while every production writer stores DASHED GUIDs - the freshest signal would have been dead in production, with a possible resume-at-stale-position announcement; my test seam mirrored the wrong shape so the suite was green over dead code. Fixed: GUID-parsed comparison (format-agnostic, unparsable pointer falls to the store arm), the seam writes the production dashed form, and a dedicated lock test seeds the production shape. Also: the stale offset-0 class-doc paragraph rewritten (C2), seams internal per file convention (I2), clamp deduplicated (I1). Tests: the old FollowMe_ResumesAtOffsetZero_ByDesign lock split into 4 (zero+plain-string, live-pointer+resume-string, store fallback, beyond-runtime clamp) + the production-shape lock = 22 follow-me tests green; suite 4151/4151 both TFMs; Release 0 warnings. OPEN: AC#4 on-device verification on 2 Echos (source mid-track, follow-me, target resumes near that position) awaits Paolo - the unit layer pins the mechanism, only the real device timing (whether the live pointer is fresh enough mid-playback) needs ears.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
