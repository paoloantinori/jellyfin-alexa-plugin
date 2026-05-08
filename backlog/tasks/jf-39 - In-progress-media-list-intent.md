---
id: JF-39
title: In-progress media list intent
status: Done
assignee: []
created_date: '2026-05-03 13:37'
updated_date: '2026-05-03 14:45'
labels:
  - enhancement
  - intent
  - voice-interaction
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add Alexa intent to list the user's in-progress media items. Inspired by Audiobookshelf skill.

Support utterances like:
- "What am I listening to?"
- "What was I watching?"
- "What's in progress?"

Implementation: Query Jellyfin API for items with partial playback progress (Items with IsInProgress or UserData.IsPlaybackPosition > 0). Return top 5 items with titles and progress info. User can then choose to resume one.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented InProgressMediaListIntentHandler listing up to 5 in-progress items with progress positions. Added locale strings and interaction models for 12 locales. 9 unit tests passing.
<!-- SECTION:FINAL_SUMMARY:END -->
