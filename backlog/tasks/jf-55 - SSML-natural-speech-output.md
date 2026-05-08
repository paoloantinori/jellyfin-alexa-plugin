---
id: JF-55
title: SSML natural speech output
status: Done
assignee: []
created_date: '2026-05-03 13:39'
updated_date: '2026-05-03 19:04'
labels:
  - enhancement
  - ux
  - voice
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Use SSML markup for more natural speech output in Alexa responses. Inspired by the competing infinityofspace/jellyfin_alexa_skill which uses break tags between title and artist.

Currently the plugin returns plain text speech. SSML can make responses sound more natural.

Implementation:
1. Add SSML break tags between song title and artist name (e.g., "Playing <break time='300ms'/> {song} by {artist}")
2. Use prosody for emphasis on key information
3. Add natural pauses between list items when reporting multiple matches
4. Use whisper or reduced volume for "now playing" confirmations to avoid interrupting music start
5. Ensure all SSML is locale-aware and validated against Alexa's SSML subset
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added SSML natural speech output with break tags, emphasis, and say-as elements for key Alexa responses (NowPlaying, disambiguation, recommendations, welcome). Added TellSsml/AskSsml/GetSsml helpers to BaseHandler, SSML keys to en-US and it-IT locales. Graceful fallback to plain text when SSML keys are missing. 9 new tests for SSML helpers.
<!-- SECTION:FINAL_SUMMARY:END -->
