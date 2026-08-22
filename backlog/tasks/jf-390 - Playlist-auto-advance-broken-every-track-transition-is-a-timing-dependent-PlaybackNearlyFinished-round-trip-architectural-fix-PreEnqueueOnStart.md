---
id: JF-390
title: >-
  Playlist auto-advance broken: every track transition is a timing-dependent
  PlaybackNearlyFinished round-trip (architectural fix = PreEnqueueOnStart)
status: To Do
assignee: []
created_date: '2026-08-22 06:55'
updated_date: '2026-08-22 07:58'
labels:
  - bug
  - playback
  - queue
  - playlist
  - auto-advance
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/20'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
GH issue #20 (pjclarke1000) + ARCHITECTURAL ANALYSIS (2026-08-22):

SYMPTOM: native Jellyfin playlists on Echo Dot do not auto-advance when a track reaches its natural end. Manual Next works. PostPlay=Stop.

ROOT CAUSE (architectural, confirmed from code): BuildPlaylistPlayResponseAsync sends ONLY ONE track to Alexa (ReplaceAll). Every track transition requires a PlaybackNearlyFinished event round-trip. If that round-trip fails (event dropped, response late, Tailscale latency > 8s window), the chain breaks.

ATTEMPT 1 (PreEnqueueOnStart response from PlaybackStarted): IMPLEMENTED AND REJECTED BY PLATFORM. Amazon returns INVALID_RESPONSE "The skill's response must not contain more than 0 AudioPlayer.Play directive(s) for this request type" for PlaybackStarted. Only PlaybackNearlyFinished accepts AudioPlayer.Play. The pre-enqueue mechanism WORKS (the next track was correctly resolved and the Enqueue directive was built) but Amazon discards it. The knob is currently deployed but set to false on minix (harmless when off).

ATTEMPT 2 (pre-compute approach): NOT YET IMPLEMENTED. When a track starts (PlaybackStarted, which CAN return keep-alive), pre-compute the next track's stream URL, item metadata, and queue resolution. Store in a per-device cache. When PlaybackNearlyFinished arrives, the response is instant (no library lookups, no queue resolution, just return the pre-built directive). This stays within platform rules and should reduce the timing window from ~1-2s of library work to ~0ms on high-latency endpoints.

GH #20 updated with findings and next steps. Awaiting user's log data to confirm whether the event arrives at all on their Tailscale setup.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Reproduce: play a multi-track native Jellyfin playlist, let track 1 end naturally; verify whether PlaybackNearlyFinished arrives and what it resolves as next item
- [ ] #2 If the event arrives but fails: diagnose the response shape (check for INVALID_RESPONSE in Alexa-side logs, or any exception in Jellyfin logs)
- [ ] #3 If the event does not arrive: investigate timing (Tailscale Funnel latency) or device-specific behavior (Echo Dot vs Show)
- [ ] #4 Fix with TDD + /simplify + /code-review high
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
