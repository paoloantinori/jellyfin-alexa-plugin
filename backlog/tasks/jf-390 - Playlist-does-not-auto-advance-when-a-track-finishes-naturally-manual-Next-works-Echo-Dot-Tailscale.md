---
id: JF-390
title: >-
  Playlist does not auto-advance when a track finishes naturally (manual Next
  works; Echo Dot + Tailscale)
status: To Do
assignee: []
created_date: '2026-08-22 06:55'
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
GH issue #20 (pjclarke1000, 2026-08-21):

Native Jellyfin playlists on Echo Dot do not auto-advance when a track reaches its natural end. Manual 'next' works correctly and advances to the right track. PostPlay Behavior is Stop (user correctly expects Stop only at queue exhaustion, not per-track).

Environment: Jellyfin 10.11.11, plugin 0.11.2.0, Echo Dot, Tailscale Funnel HTTPS.

The PlaybackNearlyFinished handler (Alexa/Handler/Event/PlaybackNearlyFinishedEventHandler.cs) resolves the next item via ResolveNextItemId and plays it. The DynamicEntities Dialog collision fix (48dd9b3) does NOT apply here because the interceptor already skips AudioPlayer responses. The SessionAttributes fix (9041e47) also does not affect AudioPlayer event responses.

LIKELY AREAS:
1. The PlaybackNearlyFinished event may not be arriving (Alexa-side timing, device-specific)
2. The event may arrive but the response may fail (latency > 8s window with Tailscale)
3. ResolveNextItemId may return null for playlist queues in some path

Diagnostic questions sent to the reporter (GH comment). Awaiting logs before deeper investigation.
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
