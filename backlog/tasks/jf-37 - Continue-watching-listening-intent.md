---
id: JF-37
title: Continue watching/listening intent
status: Done
assignee: []
created_date: '2026-05-03 13:36'
updated_date: '2026-05-03 14:27'
labels:
  - enhancement
  - intent
  - voice-interaction
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add new Alexa intent to resume the last in-progress media item. Inspired by Kodi and Audiobookshelf skills.

Support utterances like:
- "Continue watching"
- "Continue listening"
- "Resume where I left off"
- "What was I watching?"

Implementation: Query Jellyfin API for the user's latest in-progress items (ItemsService with IsResumable filter or UserData playback position tracking). Resume from the saved position. Consider returning a list if multiple items are in progress.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented ContinueWatchingIntent that resumes the last in-progress media item. Queries recent items (last 30 days) then checks IUserDataManager for PlaybackPositionTicks > 0 and Played == false. Resumes audio with offset, supports video via VideoApp. Registered in controller with IUserDataManager dependency, all 12 interaction models and locale files updated, 8 unit tests passing.
<!-- SECTION:FINAL_SUMMARY:END -->
