---
id: JF-38
title: Chapter navigation for audiobooks
status: Done
assignee: []
created_date: '2026-05-03 13:36'
updated_date: '2026-05-03 14:40'
labels:
  - enhancement
  - intent
  - voice-interaction
  - audiobook
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add Alexa intent for chapter-based navigation in audiobooks and podcasts. Inspired by Audiobookshelf skill.

Support utterances like:
- "Next chapter"
- "Previous chapter"
- "Go to chapter {number}"

Implementation: Use Jellyfin media item metadata to access chapter information. Map Next/Previous intents to chapter boundaries rather than track boundaries when playing audiobook/podcast media types. May require extending the interaction model with CHAPTER_NUMBER slot.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented GoToChapterIntentHandler with next/previous/specific chapter navigation for audiobooks. Uses IChapterManager.GetChapters() and PlayState.PositionTicks for position-aware chapter seeking. Added all locale strings and interaction model updates for 12 locales. 8 unit tests passing.
<!-- SECTION:FINAL_SUMMARY:END -->
