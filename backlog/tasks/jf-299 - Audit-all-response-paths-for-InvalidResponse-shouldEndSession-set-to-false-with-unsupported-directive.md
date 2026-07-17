---
id: JF-299
title: >-
  Audit all response paths for InvalidResponse "shouldEndSession set to false"
  with unsupported directive
status: Done
assignee:
  - claude
created_date: '2026-06-18 08:37'
updated_date: '2026-06-18 15:20'
labels:
  - bug
  - alexa
  - audio-player
dependencies: []
references:
  - >-
    minix jellyfin logs 2026-06-18 ~08:20-08:30 (FTL InvalidResponse; ERR
    Playback failed item 37b25430...)
  - Alexa/Handler/BaseHandler.cs (BuildAudioPlayerResponse and response helpers)
  - 'CLAUDE.md: ''AudioPlayer event restrictions'''
  - 'memory: feedback_should_end_session.md'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Symptom (found in logs while diagnosing the song-routing bug)

Repeated `[FTL]` errors in the Jellyfin plugin logs (minix `jellyfin` container, 2026-06-18):
```
[FTL] Alexa error: InvalidResponse category=SkillError - The following directives are not supported: Response may not have shouldEndSession set to false
```
at 08:20:27, 08:20:49, 08:25:26, 08:29:51. Alexa then speaks `"Qualcosa è andato storto. Per favore riprova."` ("Something went wrong. Please try again.").

Also seen nearby: `[ERR] Playback failed for item 37b25430-d81a-3bd6-4dbd-10a044ec2cda at offset 0ms` (08:20:33) — may be related or separate.

## Root cause (hypothesis, to confirm)

A response path returns `shouldEndSession = false` alongside a directive Amazon rejects when paired with an open session (e.g. an `AudioPlayer.Play`/`Stop` directive, or an `AudioPlayer` event response that may only contain `AudioPlayer.Play`). This matches the project's documented `feedback_should_end_session` pitfall and CLAUDE.md's "AudioPlayer event restrictions" (PlaybackFinished/PlaybackNearlyFinished can ONLY return AudioPlayer.Play — no outputSpeech, no reprompt, no shouldEndSession=false).

## Scope (per user)

The user noted this likely recurs in **multiple** handler/response paths, not just one. This task is a **full audit** of every response-building path, not a single-point fix.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Audit EVERY response-building path that can return a directive + shouldEndSession, not just the one seen in logs: AudioPlayer.Play/Stop handlers, VideoApp.Launch (audiobook) handlers, Dialog.ElicitSlot handlers (e.g. FindSongIntentHandler.BuildElicitSlotResponse), all AudioPlayer event handlers (PlaybackNearlyFinished/PlaybackFinished/PlaybackStarted), and any BaseHandler response helpers
- [ ] #2 Identify each path that returns shouldEndSession=false together with a directive Amazon does not allow alongside it (per CLAUDE.md 'AudioPlayer event restrictions': PlaybackFinished/PlaybackNearlyFinished may return ONLY AudioPlayer.Play — no outputSpeech/reprompt/shouldEndSession=false)
- [ ] #3 Fix each occurrence so shouldEndSession is set correctly for the directive type (true for AudioPlayer.Play/Stop and VideoApp.Launch; events return only the allowed directive)
- [ ] #4 Regression coverage added (unit tests asserting shouldEndSession per response type, and/or an NLU/simulator check)
- [ ] #5 On-device / logs verified: no new [FTL] InvalidResponse 'Response may not have shouldEndSession set to false' after the fix; the 'Qualcosa è andato storto' responses stop
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
FIXED + verified on device. Root cause: PlaybackStartedEventHandler returned shouldEndSession=false (added in 122d1fb "Keep session open for ... PlaybackStarted" to route AMAZON.StopIntent), but Amazon REJECTS shouldEndSession=false on AudioPlayer EVENT responses → InvalidResponse ("Qualcosa è andato storto. Per favore riprova.") on EVERY playback since 2026-06-06. Fix: return BuildKeepAliveResponse() (shouldEndSession=null) — the valid event-ack already used by PlaybackFailed/PlaybackStopped. TDD: added PlaybackStarted_Handle_DoesNotSetShouldEndSessionFalse (RED→GREEN). Audit confirmed ONLY PlaybackStarted was the culprit; the other 4 event handlers already use BuildKeepAliveResponse/BuildEndSessionResponse. Commit 8ad92cb on fix/playback-started-invalidresponse-jf299. Deployed to minix (AlexaSkill_0.8.0.0), config survived (no wipe). Device-verified: (1) playback works, (2) 0 ExceptionEncountered in logs post-fix (was firing on every playback), (3) "Alexa, ferma"/Stop STILL routes correctly — proving the shouldEndSession=false was never actually keeping the session open (Amazon rejected it), so the fix loses nothing.
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
<!-- DOD:END -->
