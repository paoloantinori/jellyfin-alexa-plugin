---
id: JF-123
title: Early audio device capability check
status: Done
assignee: []
created_date: '2026-05-12 04:45'
updated_date: '2026-05-12 12:12'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
New AudioDeviceCapabilityInterceptor gates audio requests on non-audio devices with localized error message. Registered early in pipeline. 10 unit tests. Build clean, 1087 tests pass.
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
