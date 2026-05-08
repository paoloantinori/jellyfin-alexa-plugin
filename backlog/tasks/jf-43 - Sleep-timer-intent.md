---
id: JF-43
title: Sleep timer intent
status: Done
assignee: []
created_date: '2026-05-03 13:37'
updated_date: '2026-05-03 15:09'
labels:
  - enhancement
  - intent
  - voice-interaction
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add Alexa sleep timer that stops playback after a specified duration. Inspired by Audiobookshelf skill's token-encoded deadline pattern.

Support utterances like:
- "Stop playing in 30 minutes"
- "Set a sleep timer for 1 hour"
- "Stop playing after this album"

Implementation: Audiobookshelf uses an elegant pattern where the stop deadline is encoded into the AudioPlayer token. When PlaybackNearlyFinished fires, the handler checks if the deadline has passed and stops instead of enqueuing the next track. No external scheduler or background task needed. Duration slot (AMAZON.DURATION or custom minutes slot) required.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented SleepTimerIntentHandler with token-encoded deadline pattern. Deadline stored in AudioPlayer token as "itemId|sleep:ticks". Modified PlaybackNearlyFinishedEventHandler to check deadline and stop playback when expired. Supports cancellation. 6 handler tests + full suite (381 total) passing.
<!-- SECTION:FINAL_SUMMARY:END -->
