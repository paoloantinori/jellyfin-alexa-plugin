---
id: JF-217
title: Implement GetNextItem and GetPreviousItem handlers (Alexa.Audio.PlayQueue)
status: To Do
assignee: []
created_date: '2026-05-25 20:10'
labels: []
milestone: m-3
dependencies: []
references:
  - >-
    https://developer.amazon.com/en-US/docs/alexa/device-apis/alexa-audio-playqueue.html
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement the `Alexa.Audio.PlayQueue.GetNextItem` and `Alexa.Audio.PlayQueue.GetPreviousItem` directive handlers for continuous playback.

These handlers enable:
- Automatic queue progression (gapless playback)
- "Alexa, next" voice command
- "Alexa, previous" voice command
- Queue display in the Alexa app

**Queue management strategy:**
When GetPlayableContent returns a content ID for an artist/album/playlist, the skill should:
1. Fetch all tracks from Jellyfin for that artist/album/playlist
2. Store the track list in a session-scoped queue (in-memory dict keyed by Alexa session ID)
3. Track the current position in the queue
4. Return the next/previous track's stream URL when requested

**GetNextItem request:**
```json
{
  "header": { "namespace": "Alexa.Audio.PlayQueue", "name": "GetNextItem" },
  "payload": {
    "audioItem": {
      "id": "jellyfin-artist-abc123",
      "playbackInfo": { "stream": { "url": "current-track-url" } }
    },
    "customerId": "..."
  }
}
```

**GetNextItem response:**
```json
{
  "header": { "namespace": "Alexa.Audio.PlayQueue", "name": "GetNextItemResponse" },
  "payload": {
    "audioItem": {
      "id": "jellyfin-track-xyz789",
      "title": "Comfortably Numb",
      "artist": "Pink Floyd",
      "album": "The Wall",
      "durationInMilliseconds": 382000,
      "art": { "sources": [{"url": "https://..."}] },
      "stream": {
        "url": "https://jellyfin.example.com/Audio/xyz789/stream?static=true",
        "offsetInMilliseconds": 0
      }
    }
  }
}
```

**Reference:** https://developer.amazon.com/en-US/docs/alexa/device-apis/alexa-audio-playqueue.html
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 GetNextItem returns the next track for artist/album/playlist queues
- [ ] #2 GetPreviousItem returns the previous track in the queue
- [ ] #3 Queue state maintained per session (in-memory, keyed by content ID)
- [ ] #4 Handles end-of-queue gracefully (returns appropriate response)
- [ ] #5 Unit tests for queue traversal scenarios
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
