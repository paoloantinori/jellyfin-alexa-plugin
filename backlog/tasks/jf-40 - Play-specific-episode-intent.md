---
id: JF-40
title: Play specific episode intent
status: Done
assignee: []
created_date: '2026-05-03 13:37'
updated_date: '2026-05-03 14:51'
labels:
  - enhancement
  - intent
  - voice-interaction
  - video
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add Alexa intent to play a specific TV series episode by season and episode number. Inspired by Kodi/Kanzi skill.

Support utterances like:
- "Play season 4 episode 10 of The Office"
- "Play episode 5 of season 2 of Breaking Bad"
- "Play The Office season 4 episode 10"

Implementation: Add SEASON_NUMBER and EPISODE_NUMBER slots to the interaction model. Query Jellyfin API for the series by name, then filter by season/episode index. Requires multi-slot dialog or compound slot handling.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented PlayEpisodeIntentHandler with 3-slot series/season/episode navigation. Queries Series by name, filters episodes by ParentIndexNumber/IndexNumber. Returns VideoAppLaunchDirective. 7 unit tests passing.
<!-- SECTION:FINAL_SUMMARY:END -->
