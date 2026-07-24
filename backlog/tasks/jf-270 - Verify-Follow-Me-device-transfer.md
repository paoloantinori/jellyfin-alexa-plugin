---
id: JF-270
title: Verify Follow Me device transfer
status: To Do
assignee: []
created_date: '2026-06-08 09:31'
updated_date: '2026-07-13 20:16'
labels:
  - e2e
  - playback
  - multi-device
milestone: m-4
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/FollowMeIntentHandler.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
FollowMeIntentHandler transfers playback between Alexa devices. Never tested — requires 2+ physical devices. Need to:
1. Start playback on device A
2. Say "follow me" / "transfer playback"
3. Move to device B, verify playback continues from same position
4. Verify queue transfers correctly
5. Test session continuity (voice commands work on device B)

Requires physical multi-device setup — cannot be fully automated.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
