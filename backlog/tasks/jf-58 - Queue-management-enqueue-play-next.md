---
id: JF-58
title: 'Queue management (enqueue, play next)'
status: Done
assignee: []
created_date: '2026-05-03 13:39'
updated_date: '2026-05-03 19:18'
labels:
  - enhancement
  - intent
  - queue
  - voice-interaction
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement queue management allowing users to add items to queue and control playback order. Inspired by Music Assistant and Spotify Alexa integration.

Support utterances like:
- "Add this to my queue"
- "Play this next"
- "What's in my queue?"
- "Clear my queue"

Implementation:
1. Maintain a per-user playback queue (separate from the current session-based approach)
2. Use PlaybackNearlyFinished event to enqueue next item from the managed queue
3. "Play next" inserts at front of queue; "Add to queue" appends
4. Queue state persisted in Jellyfin session data
5. Leverage the existing PlaybackNearlyFinishedEventHandler for seamless queue advancement
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
