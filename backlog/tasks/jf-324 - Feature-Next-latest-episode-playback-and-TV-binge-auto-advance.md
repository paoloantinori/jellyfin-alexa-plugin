---
id: JF-324
title: 'Feature: Next/latest-episode playback and TV binge auto-advance'
status: To Do
assignee: []
created_date: '2026-07-12 15:00'
updated_date: '2026-07-13 20:16'
labels:
  - feature
  - tv
  - video
milestone: m-9
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayEpisodeIntentHandler.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Event/PlaybackNearlyFinishedEventHandler.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Video/TV is the shallowest surface (functional review 2026-07-12). Today `PlayEpisodeIntentHandler` REQUIRES an explicit season AND episode number (else it responds "didn't catch the episode number"); there is no way to say "play the next / latest episode of {series}", and there is no video PostPlay — `PlaybackNearlyFinishedEventHandler`/`PlaybackFinishedEventHandler` enqueue only music radio tracks, so a finished episode just stops. This is the highest-value gap a TV user hits in the first week.

Deliver two capabilities using Jellyfin's NextUp API:
1. New utterances/intent: "play the next episode of {series}", "play the latest episode of {series}", "continue watching {series}" resolving to the correct next unwatched episode via NextUp (respect per-user watched state and library/content gating).
2. Auto-advance: when an episode finishes and the user is bingeing a series, enqueue the next episode via the VideoApp launch path — mirroring the existing music AutoPlay mechanism in PlaybackNearlyFinished, but for video. Within-session auto-advance is reliable; cross-session continuation relies on relaunch/resume (note the platform limits).

Respect platform constraints already documented in CLAUDE.md: VideoApp.Launch for video, AudioPlayer-event handlers must never set shouldEndSession=false, Next/Stop during playback may be claimed by the default service. Add samples to all 17 locales (it-IT via YAML template) and locale response strings, plus unit + NLU tests.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 User can say 'play the next episode of {series}' and get the correct next unwatched episode via Jellyfin NextUp
- [ ] #2 User can say 'play the latest episode of {series}' and get the most recent episode
- [ ] #3 PlayEpisode still supports explicit season+episode, and no longer hard-fails when only a series is given (falls back to next-up)
- [ ] #4 When a video episode finishes during a session, the next episode auto-advances via VideoApp without a manual command
- [ ] #5 Per-user watched state and library/content-access gating are respected in episode selection
- [ ] #6 Samples added to all 17 locales (it-IT via YAML template) with locale response strings
- [ ] #7 Unit and NLU tests cover next/latest-episode routing and the auto-advance path
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
