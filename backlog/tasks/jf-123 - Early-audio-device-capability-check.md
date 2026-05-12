---
id: JF-123
title: Early audio device capability check
status: To Do
assignee: []
created_date: '2026-05-12 04:45'
labels:
  - enhancement
  - reliability
  - ux
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add an early handler that checks if the requesting Alexa device supports the AudioPlayer interface. If not, return a graceful message before making any Jellyfin API calls. This prevents unnecessary server load and confusing errors on incompatible devices.

Inspired by AskPlex's CheckAudioInterfaceHandler which is registered as the first handler and fails fast on non-audio devices.

Implementation: Add a handler early in the RequestPipeline that inspects `request.context.System.device.supportedInterfaces` for `AudioPlayer`. Return a localized error message if absent.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Handler chain checks AudioPlayer interface support before making any Jellyfin API calls
- [ ] #2 Non-audio devices receive a graceful 'this device doesn't support audio playback' message
- [ ] #3 Message is localized in all 12 locales
- [ ] #4 Check runs early in the request pipeline (fail-fast)
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
