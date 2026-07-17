---
id: JF-180
title: Upgrade QueryArtistLibrary from text list to image carousel
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
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/QueryArtistLibraryIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Upgrade QueryArtistLibraryIntentHandler from text list to image carousel. When users ask "which albums by Radiohead", album art carousel is more engaging than text rows. User can tap to play.

**Current state:** Uses `TryAttachListDirective` with text-only rows.

**Implementation:**
- Build ListDisplayItem objects with album cover image URLs
- Replace TryAttachListDirective with TryAttachCarouselDirective
- Verify tap-to-play works

**Scope:** Single handler change.

**Depends on:** JF-177
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
- [ ] #1 QueryArtistLibrary shows album art carousel instead of text list
- [ ] #2 Tapping an album plays it
- [ ] #3 Non-APL devices unchanged
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Replaced TryAttachListDirective with TryAttachCarouselDirective in QueryArtistLibraryIntentHandler. Image URLs were already being populated via GetImageUrl. Single-line change. 1656 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
