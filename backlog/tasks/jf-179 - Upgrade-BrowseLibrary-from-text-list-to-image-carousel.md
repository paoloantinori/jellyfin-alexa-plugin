---
id: JF-179
title: Upgrade BrowseLibrary from text list to image carousel
status: Done
assignee: []
created_date: '2026-05-18 13:10'
updated_date: '2026-05-18 15:32'
labels:
  - apl
  - carousel
  - ux
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/BrowseLibraryIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Upgrade BrowseLibraryIntentHandler from text list APL to image carousel. When users ask "what albums do we have" or "show me movies", album covers and movie posters are more useful than text rows. Users can tap an item to play it.

**Current state:** Uses `TryAttachListDirective` with text-only `ListDisplayItem` rows.

**Implementation:**
- Build `ListDisplayItem` objects with image URLs for each result
- Replace `TryAttachListDirective` call with `TryAttachCarouselDirective`
- Verify tap-to-play works via existing carouselTap handler

**Scope:** Single handler change.

**Depends on:** JF-177 (TryAttachCarouselDirective helper)
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
- [ ] #1 BrowseLibrary shows image carousel on APL devices instead of text list
- [ ] #2 Album/movie items show cover art/poster images
- [ ] #3 Tapping a carousel item plays it
- [ ] #4 Non-APL devices unchanged
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Replaced TryAttachListDirective with TryAttachCarouselDirective in BrowseLibraryIntentHandler. Image URLs were already being populated via GetImageUrl. Single-line change. 1656 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
