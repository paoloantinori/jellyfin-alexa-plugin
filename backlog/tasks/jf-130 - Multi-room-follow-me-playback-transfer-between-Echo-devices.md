---
id: JF-130
title: Multi-room "follow me" playback transfer between Echo devices
status: To Do
assignee: []
created_date: '2026-05-12 04:45'
labels:
  - enhancement
  - multi-room
  - playback
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add multi-room playback transfer support, allowing users to move playing media between Echo devices with a voice command like "follow me" or "move to the kitchen".

Inspired by SmartSkills MediaServer for Lyrion Media Server (LMS), which has a certified Alexa skill with 129 intents including multi-room group control and a "follow me" command. This is one of the most sophisticated media server Alexa skills on the market.

Implementation: Use Alexa's `Stop` directive on the source device, then `AudioPlayer.Play` with offset on the target device. Requires tracking which device is playing what, and mapping device IDs to room names. May require multi-device skill session management.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 'Follow me' or 'move to {room}' command transfers playback to a different Echo device
- [ ] #2 Playback resumes at the same position on the target device
- [ ] #3 Source device stops playback cleanly
- [ ] #4 Works with grouped Echo devices in the same household
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
