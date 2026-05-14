---
id: JF-118
title: AudioItem metadata with album art for AudioPlayer
status: Done
assignee: []
created_date: '2026-05-12 04:44'
updated_date: '2026-05-12 11:30'
labels:
  - enhancement
  - visual
  - playback
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Attach album art and artist art as metadata on AudioItem objects in AudioPlayer directives. This makes album art appear on Echo Show/Spot devices during playback even outside APL templates, leveraging the built-in AudioPlayer visual display.

Inspired by AskPlex which attaches `art` and `thumbnail` to AudioItem metadata on PlayDirective streams.

Implementation: In `GetStreamUrl()` and related AudioPlayer directive builders, add `Metadata` with `Art` (album art URL) and `BackgroundImage` (artist/album background) from Jellyfin's `/Items/{id}/Images` endpoints.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Album art displays on Echo Show during audio playback via AudioPlayer metadata
- [ ] #2 Art URLs use Jellyfin image API endpoints
- [ ] #3 Missing art gracefully handled (no broken images)
- [ ] #4 Both AudioItem.Art and AudioItem.BackgroundImage.Uri populated when available
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Already fully implemented. `BuildAudioPlayerResponse()` in BaseHandler creates `AudioItemMetadata` with Title, Subtitle, Art, and BackgroundImage using Jellyfin's `/Items/{id}/Images/Primary` endpoint. All 4 acceptance criteria met. 20 existing CoverArt tests all pass.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
- [ ] #2 dotnet build passes with 0 errors
- [ ] #3 dotnet test passes
- [ ] #4 No new compiler warnings introduced
- [ ] #5 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #6 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #7 NLU test fixtures updated if interaction model changed
- [ ] #8 E2E test added for new intent or handler logic
- [ ] #9 Locale response strings added to all 12 locales
<!-- DOD:END -->
