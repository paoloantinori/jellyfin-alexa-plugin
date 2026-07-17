---
id: JF-281
title: Verify voice profiles with real multi-user recognition
status: To Do
assignee: []
created_date: '2026-06-08 09:32'
updated_date: '2026-07-13 20:17'
labels:
  - e2e
  - voice-profiles
  - multi-device
milestone: m-5
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/LearnMyVoiceIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/WhoAmIIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
LearnMyVoiceIntent and WhoAmIIntent handle voice profile registration and identification. Registration flow is unit-tested but actual multi-user voice recognition has never been verified with real voices. Need to:
1. Register voice profile for user A
2. Register voice profile for user B
3. Have user A speak — verify correct Jellyfin user is identified
4. Have user B speak — verify different Jellyfin user
5. Test unrecognized voice — verify fallback behavior
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
