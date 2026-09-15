---
id: JF-566
title: >-
  Repeat during VideoApp audiobook playback restarts the previous audio track
  (book launches are not recorded as device last-played)
status: To Do
assignee: []
created_date: '2026-09-15 04:18'
labels:
  - bug
  - audiobook
  - playback
  - repeat
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-562 code review. RepeatIntentHandler resolves the current item from context.AudioPlayer.Token, then session.FullNowPlayingItem, then the DeviceQueueManager last-played record. A VideoApp-launched audiobook (NativeControlsForBooks on) is recorded NOWHERE: PlayBookIntentHandler launches books via BaseHandler.BuildVideoAppAudioResponse (no RecordLastPlayed call) and LastPlayedResponseInterceptor deliberately skips the audiobook concat URL shape to preserve chapter accuracy (its remarks, Pipeline/LastPlayedResponseInterceptor.cs). So a repeat arriving mid-book resolves the PREVIOUS music track (stale token) and restarts it via AudioPlayer.Play, destroying nothing audible (the book keeps streaming) but answering nonsense.

Candidate fix directions (need design care because the recording skip is deliberate):
- Record the book PARENT id in the audiobook launch path (not in the interceptor's URL parsing, which cannot distinguish book concat from other shapes without weakening the chapter-precision rule) so GetLastPlayedItemId returns the book mid-play; RepeatIntentHandler would then answer the honest CannotRepeatContent tell.
- Check interactions first: LaunchRequestHandler's no-token resume-offer path (BuildDeviceLastPlayedOffer) would start offering the BOOK on skill open for devices whose last play was a book; verify that path handles AudioBook items correctly (it may already, via FindLastPlayedItemWithProgress).

Currently documented as a Known limitation in RepeatIntentHandler's class doc.
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
