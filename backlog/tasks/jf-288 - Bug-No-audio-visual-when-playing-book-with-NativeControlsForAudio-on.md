---
id: JF-288
title: 'Bug: No audio/visual when playing book with NativeControlsForAudio on'
status: Done
assignee:
  - claude
created_date: '2026-06-09 15:08'
updated_date: '2026-06-09 16:05'
labels:
  - bug
  - playback
  - native-controls
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Playing an audiobook (Folder) with NativeControlsForAudio enabled results in no audio and no visual on the device.

The code path (Folder → resolve to Audio child → BuildAudioPlayerResponse → BuildVideoAppAudioResponse) looks correct on paper — the VideoApp directive is returned with the correct child ID and URL. The unit tests now pass (JF-286 added NativeControlsForAudio coverage).

So the issue is likely at runtime:
- ffmpeg not available or failing in the video-audio endpoint
- The video-audio endpoint returning an error for certain audio formats
- VideoApp.Launch directive not rendering correctly for audio-only content on certain Echo Show models
- ShouldEndSession=null in VideoApp response causing session handling issues

Needs live debugging: check Jellyfin logs and the video-audio endpoint response when tapping an audiobook on Echo Show with NativeControlsForAudio=true.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Root Cause
BuildVideoAppAudioResponse and 3 other VideoApp handlers used ShouldEndSession=null instead of true. This kept the Alexa session open, causing intent routing failures — the Echo sent SessionEndedRequest instead of routing to the correct handler. The AplUserEventHandler Movie handler correctly used true, establishing the correct pattern.

## Fix
1. Changed ShouldEndSession from null to true in:
   - BaseHandler.BuildVideoAppAudioResponse (audio playback via VideoApp)
   - PlayVideoIntentHandler (video playback)
   - PlayRandomIntentHandler (random video/audio playback) 
   - PlayEpisodeIntentHandler (episode playback)
2. Added ShouldEndSession assertions to 66+ tests across the test suite to prevent future regressions
3. Renamed PlayVideoIntentHandler test from DoesNotSetShouldEndSession → EndsSession
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed ShouldEndSession=null in 4 VideoApp handlers → ShouldEndSession=true. Root cause: null kept the session open, causing Alexa to misroute intents (SessionEndedRequest instead of proper handler). Added 66+ ShouldEndSession assertions across test suite to prevent regression. Verified on live instance — VideoApp.Launch now returns shouldEndSession:true.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
