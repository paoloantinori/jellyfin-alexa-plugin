---
id: JF-176
title: Show APL carousel of recently played content on skill launch
status: Done
assignee: []
created_date: '2026-05-18 09:48'
updated_date: '2026-05-18 12:50'
labels:
  - feature
  - apl
  - ux
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/LaunchRequestHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Apl/AplHelper.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntityBuilder.cs
documentation:
  - >-
    Alexa APL Carousel documentation:
    https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-carousel.html
  - >-
    Alexa APL TouchableWrappers:
    https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-touchable.html
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When a user opens the skill with "open jellyfin player" (LaunchRequest), and the device supports APL, display a visual carousel of recently played items in reverse chronological order, including album art / poster images. Each item should be tappable to resume playback.

**Current state:**
- `LaunchRequestHandler` currently offers a voice-only resume prompt when `AudioPlayer.Token` exists
- `AplHelper` has APL detection (`DeviceSupportsApl()`) and two templates (now-playing, queue list)
- `DynamicEntityBuilder.BuildLastPlayedValues()` already queries last 15 played items from DB with 5-min cache
- `BaseHandler.GetImageUrl()` constructs Jellyfin image URLs via `Items/{id}/Images/Primary?api_key=...`
- `PluginConfiguration.AplVisualsEnabled` gates APL features

**User experience:**
- User says "open jellyfin player"
- If device supports APL → show carousel of last ~10 played items (albums, songs, movies, episodes) with images
- Tapping an item starts playback of that item
- If no history → show a welcome/browsing prompt instead
- Voice prompt should still play alongside the visual (e.g. "What would you like to play?")
- Resume logic still takes priority if audio was actively playing before the re-launch
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 APL carousel renders on LaunchRequest when device supports APL and AplVisualsEnabled is true
- [ ] #2 Carousel shows up to 10 recently played items with primary images in reverse chronological order
- [ ] #3 Tapping a carousel item starts playback of that item via a new touch intent handler
- [ ] #4 Non-APL devices fall back to current voice-only behavior unchanged
- [ ] #5 Resume-offer logic takes priority over carousel when audio was actively playing before re-launch
- [ ] #6 Empty library / no history shows a friendly visual welcome instead of an empty carousel
- [ ] #7 Works across all media types: audio, movies, episodes, audiobooks
- [ ] #8 Unit tests for carousel data building, touch handler routing, and APL fallback
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
APL carousel feature complete. 4 subtasks implemented:
- JF-176.1: APL carousel template with horizontal Sequence, tappable image cards, SendEvent with carouselTap + item ID
- JF-176.2: GetRecentlyPlayedItems query in BaseHandler with feature flag filtering, name dedup, image URLs
- JF-176.3: LaunchRequestHandler integration — shows carousel on skill launch when APL supported, resume takes priority, "RecentlyPlayed" locale string in all 17 locales
- JF-176.4: AplUserEventHandler handles carouselTap UserEvent → plays audio/video

Build: 0 errors, 0 warnings. Tests: 1647 pass. 4 commits pushed to main.
<!-- SECTION:FINAL_SUMMARY:END -->

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
