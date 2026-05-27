---
id: JF-215
title: Implement GetPlayableContent handler (Alexa.Media.Search)
status: To Do
assignee: []
created_date: '2026-05-25 20:10'
updated_date: '2026-05-25 20:11'
labels: []
milestone: m-3
dependencies: []
references:
  - >-
    https://developer.amazon.com/en-US/docs/alexa/device-apis/alexa-media-search.html
  - 'https://github.com/pantinor/jellyfin-alexa-plugin'
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement the `Alexa.Media.Search.GetPlayableContent` directive handler — the primary entry point for music playback requests.

When a user says "Alexa, play Pink Floyd on Jellyfin", Alexa sends a GetPlayableContent request with the search context. This handler must:
1. Parse the request payload to extract search terms (artistName, albumName, trackName, playlistName)
2. Determine the content type (MusicRecording, MusicAlbum, MusicArtist, Playlist)
3. Search Jellyfin for matching content using the JellyfinClient
4. Return a content identifier with metadata that Alexa uses for the Initiate flow

**Request format** (from Alexa):
```json
{
  "header": { "namespace": "Alexa.Media.Search", "name": "GetPlayableContent" },
  "payload": {
    "locale": "en-US",
    "customerId": "amzn1.ask.customer...",
    "searchTerm": "pink floyd",
    "audioItem": {
      "type": "MusicArtist",
      "artistName": "pink floyd"
    }
  }
}
```

**Response format** (to Alexa):
```json
{
  "header": { "namespace": "Alexa.Media.Search", "name": "GetPlayableContentResponse" },
  "payload": {
    "items": [{
      "id": "jellyfin-artist-abc123",
      "title": "Pink Floyd",
      "artist": "Pink Floyd",
      "art": { "sources": [{"url": "https://jellyfin.example.com/Items/abc123/Images/Primary"}] },
      "audioItem": {
        "type": "MusicArtist",
        "name": "Pink Floyd"
      },
      "playbackInfo": {
        "stream": { "url": "...", "offsetInMilliseconds": 0 }
      }
    }]
  }
}
```

**Content type mapping:**
| Alexa audioItem.type | Jellyfin search strategy |
|---------------------|------------------------|
| MusicRecording | Search tracks by name |
| MusicAlbum | Search albums by name, get all tracks |
| MusicArtist | Search artists by name, get all tracks |
| Playlist | Search playlists by name |
| Genre | Search by genre tag |

**Reference:** Amazon docs: https://developer.amazon.com/en-US/docs/alexa/device-apis/alexa-media-search.html
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 GetPlayableContent resolves track, album, artist, and playlist requests to Jellyfin items
- [ ] #2 Returns correct content ID, metadata (title, artist, album, duration), art URLs, and stream URLs
- [ ] #3 Handles 'play X on Jellyfin' for songs, albums, artists, and genres
- [ ] #4 Returns CONTENT_NOT_FOUND for unknown items
- [ ] #5 Supports locale-aware search (en-US, it-IT at minimum)
- [ ] #6 Unit tests with mocked Jellyfin responses for each content type
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
