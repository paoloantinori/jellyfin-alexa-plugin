---
id: JF-118
title: AudioItem metadata with album art for AudioPlayer
status: To Do
assignee: []
created_date: '2026-05-12 04:44'
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

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
