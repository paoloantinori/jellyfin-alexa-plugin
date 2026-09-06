---
id: JF-507
title: >-
  Audio-only resume of EAC3 video items plays 1ms and dies (raw EAC3 track
  unplayable by AudioPlayer) + INVALID_RESPONSE outputSpeech in the failure
  event chain
status: To Do
assignee: []
created_date: '2026-09-06 15:24'
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
