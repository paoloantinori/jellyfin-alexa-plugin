---
id: JF-214
title: Implement Jellyfin API client for music content lookup
status: To Do
assignee: []
created_date: '2026-05-25 20:09'
updated_date: '2026-05-25 20:11'
labels: []
milestone: m-3
dependencies: []
references:
  - >-
    https://github.com/pantinor/jellyfin-alexa-plugin/blob/main/Jellyfin.Plugin.AlexaSkill/Alexa/FuzzyMatcher.cs
  - >-
    https://github.com/pantinor/jellyfin-alexa-plugin/blob/main/Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - 'https://github.com/pantinor/jellyfin-alexa-plugin'
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Build the Jellyfin REST API client module that the Music Skill API handlers will use to search and stream music.

**Required capabilities:**
1. **Authentication**: Support both API key auth and username/password auth for Jellyfin
2. **Search**: Query Jellyfin's `/Items` endpoint with search terms (artist, album, track name)
3. **Artist lookup**: Find artists by name (exact + fuzzy match) — can reuse fuzzy matching logic from the existing Jellyfin plugin (`Jellyfin.Plugin.AlexaSkill/Alexa/FuzzyMatcher.cs`)
4. **Album/track lookup**: Get tracks by album, get albums by artist
5. **Stream URL generation**: Generate `/Audio/{id}/stream?static=true` URLs (same pattern as the existing plugin)
6. **Image URL generation**: Generate `/Items/{id}/Images/Primary` URLs for album art
7. **User resolution**: Map Alexa customer IDs to Jellyfin users via account linking or config

**Port from existing Jellyfin plugin:**
- Fuzzy matching logic from `Alexa/FuzzyMatcher.cs`
- Search patterns from `Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs` (4-tier fallback chain)
- Stream URL construction from `Alexa/Handler/BaseHandler.cs` (`GetStreamUrl()`)
- Image URL construction from `Alexa/Handler/BaseHandler.cs`
- Library filtering from `Alexa/Handler/BaseHandler.cs` (`ApplyLibraryFilter`)

**Jellyfin API endpoints needed:**
- `GET /Items?searchTerm={q}&IncludeItemTypes=Audio&Recursive=true` — search
- `GET /Artists?searchTerm={q}` — artist search
- `GET /Items?ArtistIds={id}&IncludeItemTypes=Audio` — artist's tracks
- `GET /Items?ParentId={albumId}&IncludeItemTypes=Audio` — album tracks
- `GET /Items/{id}` — item details (for duration, metadata)
- `/Audio/{id}/stream?static=true` — audio stream
- `/Items/{id}/Images/Primary` — album art
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 JellyfinClient class with async methods for search, artist lookup, album/track lookup, stream URLs, image URLs
- [ ] #2 Unit tests with mocked Jellyfin API responses
- [ ] #3 Fuzzy matching ported from existing plugin with test coverage
- [ ] #4 Stream URLs match existing plugin format: /Audio/{id}/stream?static=true
- [ ] #5 Handles connection errors and timeouts gracefully
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
