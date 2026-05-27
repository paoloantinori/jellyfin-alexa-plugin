---
id: JF-218
title: >-
  Implement optional Media.PlayQueue handlers (GetItem, SetShuffle, SetLoop,
  SetRepeat, GetView)
status: To Do
assignee: []
created_date: '2026-05-25 20:11'
labels: []
milestone: m-3
dependencies: []
references:
  - >-
    https://developer.amazon.com/en-US/docs/alexa/device-apis/alexa-media-playqueue.html
  - >-
    https://developer.amazon.com/en-US/docs/alexa/device-apis/alexa-audio-playqueue.html
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement the optional but recommended `Alexa.Media.PlayQueue` and `Alexa.Media.PlayQueue` directives:

1. **GetItem** (`Alexa.Media.PlayQueue`) — Refresh expired stream URIs. Jellyfin stream URLs don't expire, so this can return the same URL or generate a fresh one.
2. **SetShuffle** (`Alexa.Media.PlayQueue`) — Enable/disable shuffle mode on the current queue. Rearranges the in-memory track list.
3. **SetLoop** (`Alexa.Media.PlayQueue`) — Enable/disable loop mode (repeat entire queue from start after last track).
4. **SetRepeat** (`Alexa.Media.PlayQueue`) — Enable/disable repeat mode (repeat current track).
5. **GetView** (`Alexa.Media.PlayQueue`) — Return queue information for the Alexa app display. Shows currently playing + up to 10 upcoming tracks.

These enhance the voice control and graphical display experience in the Alexa app and on Echo Show devices.

**Reference:** https://developer.amazon.com/en-US/docs/alexa/device-apis/alexa-audio-playqueue.html
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 GetItem refreshes expired stream URLs with new Jellyfin stream URLs
- [ ] #2 SetShuffle toggles shuffle mode on the queue
- [ ] #3 SetLoop toggles loop mode on the queue
- [ ] #4 SetRepeat toggles repeat mode on the queue
- [ ] #5 GetView returns current queue info for Alexa app display
- [ ] #6 All handlers return proper error responses for invalid states
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
