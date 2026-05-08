---
id: JF-61
title: Multi-device playback control
status: Done
assignee: []
created_date: '2026-05-03 13:39'
updated_date: '2026-05-03 21:22'
labels:
  - enhancement
  - multi-device
  - voice-interaction
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Enable multi-device playback control allowing users to target specific Echo devices for playback. Inspired by Music Assistant and Spotify Connect patterns.

Support utterances like:
- "Play this on the living room speaker"
- "Move playback to the bedroom"
- "Play on {device name}"

Implementation:
1. List available Echo devices via Alexa API (or accept device name as slot)
2. Route AudioPlayer directives to the target device
3. Support "transfer" command to move current playback between devices
4. Maintain awareness of which device is actively playing per user

Limitations: Alexa's AudioPlayer interface is tied to the device that received the request. True multi-device control may require the Alexa Video Skill API or connected device patterns. Research feasibility as part of this task.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Research confirms whether multi-device routing is possible via Alexa custom skill AudioPlayer directives
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Not feasible with current Alexa Skills Kit APIs. AudioPlayer.Play directives are bound to the originating device — there is no target device field. No API exists to enumerate Echo devices on an account (Smart Home Discovery API is restricted to smart home endpoints). Users can natively say "Alexa, move my music to [device]" which is a platform-level feature, not accessible to custom skills. This task should remain closed until Amazon exposes a cross-device routing API for custom skills.
<!-- SECTION:FINAL_SUMMARY:END -->
