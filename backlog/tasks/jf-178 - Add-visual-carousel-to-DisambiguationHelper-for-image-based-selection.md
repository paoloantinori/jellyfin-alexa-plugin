---
id: JF-178
title: Add visual carousel to DisambiguationHelper for image-based selection
status: Done
assignee: []
created_date: '2026-05-18 13:09'
updated_date: '2026-05-18 15:15'
labels:
  - apl
  - carousel
  - disambiguation
  - ux
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/DisambiguationHelper.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlaySongIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayVideoIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayBookIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPodcastIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/AddToQueueIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayNextIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPlaylistIntentHandler.cs
documentation:
  - >-
    https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-carousel.html
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Extend `DisambiguationHelper.AskFirstMatch` to attach an image carousel when APL is available, so users can tap to select from multiple matches instead of voice-only disambiguation.

**Current state:** DisambiguationHelper.AskFirstMatch takes `(List<(Guid Id, string Name)> matches, string mediaType, string locale)` and returns voice-only response. 9 handlers use it for disambiguation.

**Implementation:**
1. Extend the match tuple to include image URLs: `List<(Guid Id, string Name, string? ArtUrl)>` (keep backward-compatible overload)
2. In AskFirstMatch, when items have art URLs, build a `List<ListDisplayItem>` and call `TryAttachCarouselDirective` on the response
3. Use a new touch action `"disambiguateSelect"` so AplUserEventHandler can route disambiguation taps differently from play taps
4. Add `"disambiguateSelect"` case to AplUserEventHandler that extracts the item ID and plays it (similar to carouselTap/selectItem)
5. Update all 9 callers to pass image URLs:
   - PlayArtistSongsIntentHandler — artist image
   - PlayAlbumIntentHandler — album cover
   - PlaySongIntentHandler — album cover from song
   - PlayVideoIntentHandler — movie poster
   - PlayBookIntentHandler — book cover
   - PlayPodcastIntentHandler — podcast art
   - AddToQueueIntentHandler — item art
   - PlayNextIntentHandler — item art
   - PlayPlaylistIntentHandler — playlist art (if available)

**Depends on:** JF-177 (TryAttachCarouselDirective helper)

**Impact:** ALL disambiguation flows get visual selection in one change. When a user says "play thriller" and there are multiple matches, they SEE album covers and tap the right one instead of guessing by voice.
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 DisambiguationHelper has overload accepting (Guid Id, string Name, string? ArtUrl) tuples
- [ ] #2 Original (Guid Id, string Name) overload still works for backward compatibility
- [ ] #3 Image carousel attached when APL available and matches have art URLs
- [ ] #4 Voice disambiguation prompt still plays alongside carousel
- [ ] #5 disambiguateSelect touch action in AplUserEventHandler plays tapped item
- [ ] #6 All 9 handlers updated to pass image URLs
- [ ] #7 Unit tests for extended DisambiguationHelper with carousel
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added AskFirstMatch overload accepting (Guid Id, string Name, string? ArtUrl) tuples + Context. When APL is available, attaches image carousel of matches alongside voice disambiguation prompt. All 9 disambiguation handlers (PlayArtistSongs, PlayAlbum, PlaySong, PlayVideo, PlayBook, PlayPodcast, AddToQueue, PlayNext, PlayPlaylist) updated to pass image URLs. SearchMedia also updated. Added DisambiguateCarouselTitle locale string to all 17 locales. Original overload preserved for backward compat. 6 new tests. 1656 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
