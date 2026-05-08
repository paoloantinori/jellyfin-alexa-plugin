---
id: JF-57
title: PlaybackController interface for hardware buttons
status: Done
assignee: []
created_date: '2026-05-03 13:39'
updated_date: '2026-05-03 17:16'
labels:
  - enhancement
  - ux
  - playback
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Ensure hardware button presses on Echo devices (play/pause/next/previous on the device itself) are properly handled via the PlaybackController interface. Inspired by the official ASK audio player sample.

Currently the plugin handles voice intents but may not properly respond to the PlaybackController.PlayCommandIssued, PauseCommandIssued, NextCommandIssued, PreviousCommandIssued events from hardware buttons.

Implementation:
1. Verify all PlaybackController events have corresponding handlers
2. Map PlayCommandIssued → resume with saved offset
3. Map PauseCommandIssued → stop (saves offset via PlaybackStopped event)
4. Map NextCommandIssued/PreviousCommandIssued → queue navigation
5. Test with physical Echo device buttons and remote controls
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
PlaybackController interface already fully implemented. PlayIntentHandler handles PlayCommandIssued, PauseIntentHandler handles PauseCommandIssued, NextIntentHandler handles NextCommandIssued, PreviousIntentHandler handles PreviousCommandIssued. All four hardware button events are mapped to their corresponding voice intent handlers via CanHandle() checks.
<!-- SECTION:FINAL_SUMMARY:END -->
