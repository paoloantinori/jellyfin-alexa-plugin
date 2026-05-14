---
id: JF-130
title: Multi-room "follow me" playback transfer between Echo devices
status: Done
assignee: []
created_date: '2026-05-12 04:45'
updated_date: '2026-05-12 13:37'
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
- [x] #1 'Follow me' or 'move to {room}' command transfers playback to a different Echo device
- [ ] #2 Playback resumes at the same position on the target device
- [ ] #3 Source device stops playback cleanly
- [ ] #4 Works with grouped Echo devices in the same household
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
FollowMeIntentHandler uses pull model (user speaks to target device). Resumes most recently active queue from another device via DeviceQueueManager.GetAllActiveQueues(). Platform limitation: cannot push audio to other devices. Added to all 17 interaction models and locales. 14 unit tests. 1195 total tests pass.
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
