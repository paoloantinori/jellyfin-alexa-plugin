---
id: JF-216
title: Implement Initiate and Reinitiate handlers (Alexa.Media.Playback)
status: To Do
assignee: []
created_date: '2026-05-25 20:10'
labels: []
milestone: m-3
dependencies: []
references:
  - >-
    https://developer.amazon.com/en-US/docs/alexa/device-apis/alexa-media-playback.html
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement the `Alexa.Media.Playback.Initiate` and `Alexa.Media.Playback.Reinitiate` directive handlers.

**Initiate** is sent by Alexa after GetPlayableContent succeeds and Alexa decides to play the content. It receives the content ID and returns the actual stream URL for immediate playback.

**Reinitiate** handles device handoff — when a user moves playback from one Echo device to another. It receives the origin device info and current offset, and returns a fresh stream URL with the correct position.

**Initiate request format:**
```json
{
  "header": { "namespace": "Alexa.Media.Playback", "name": "Initiate" },
  "payload": {
    "audioItem": {
      "id": "jellyfin-artist-abc123",
      "type": "MusicArtist",
      "name": "Pink Floyd",
      "playbackInfo": {
        "stream": { "url": "placeholder" }
      }
    },
    "customerId": "amzn1.ask.customer..."
  }
}
```

**Initiate response format:**
```json
{
  "header": { "namespace": "Alexa.Media.Playback", "name": "InitiateResponse" },
  "payload": {
    "audioItem": {
      "stream": {
        "url": "https://jellyfin.example.com/Audio/track123/stream?static=true",
        "offsetInMilliseconds": 0,
        "expiryTime": "2026-05-25T21:00:00Z",
        "progressReport": {
          "progressReportIntervalInMilliseconds": 10000
        }
      }
    }
  }
}
```

Key implementation detail: The content ID from GetPlayableContent must encode enough info to resolve to a Jellyfin track or queue. For artists/albums, the first track's stream URL is returned, and GetNextItem handles the queue.

**Reference:** https://developer.amazon.com/en-US/docs/alexa/device-apis/alexa-media-playback.html
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Initiate returns valid stream URL for content IDs from GetPlayableContent
- [ ] #2 Reinitiate handles device handoff with correct offset
- [ ] #3 Stream URLs use /Audio/{id}/stream?static=true format
- [ ] #4 Handles expired content IDs gracefully (returns error)
- [ ] #5 Unit tests for Initiate and Reinitiate flows
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
