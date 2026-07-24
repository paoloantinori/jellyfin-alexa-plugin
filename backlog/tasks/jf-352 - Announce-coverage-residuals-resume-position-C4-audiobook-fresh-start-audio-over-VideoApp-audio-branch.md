---
id: JF-352
title: >-
  Announce coverage residuals: resume-position (C4), audiobook fresh-start,
  audio-over-VideoApp, audio-branch
status: Done
assignee: []
created_date: '2026-07-19 06:51'
updated_date: '2026-07-19 08:52'
labels:
  - ux
  - announce
  - video
  - follow-up
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Out-of-scope residuals surfaced by the JF-349 /code-review high (the 3 handlers there announce 'Now playing X' unconditionally). Decide whether to address:

1. **C4 (resume-position awareness)** — AplUserEventHandler Movie branch + SearchMedia PlayItem video branch announce 'Now playing X' with no UserData lookup, while PlayVideo announces 'Resuming X from position Y' when resumeTicks > 0. Same action (launch a half-watched movie via VideoApp) produces different announce wording by entry point; a carousel/search launch starts from 0 with no position warning. Fix = fetch PlaybackPositionTicks in those two branches + use ResumingVideo-style announce when > 0.

2. **F2 (audiobook fresh-start)** — PlayBookIntentHandler fresh-start (NativeControlsForBooks=true, not resuming, ~line 282) returns BuildVideoAppAudioResponse bare; only the resuming branch announces (ResumingBook). Add a now-playing/book announce on fresh-start.

3. **F3 (audio-over-VideoApp)** — BaseHandler.BuildAudioPlayerResponse routes Audio/AudioBook items (GetVideoAppForAudio / NativeControlsForBooks) to BuildVideoAppAudioResponse, which has no OutputSpeech; most callers don't add one. Recommend-style callers do. Decide a default announce for audio-via-VideoApp.

4. **F4 (AudioPlayer audio-branch)** — SearchMedia.PlayItem audio branch + general AudioPlayer launches: decide whether audio launches should announce now-playing too (RecommendIntentHandler audio branch already does; others don't). Cross-cuts the music-delivery-choice feature.

These are UX-consistency enhancements, not bugs. Triage individually.
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
