---
id: JF-271
title: Verify Proactive Events notifications
status: To Do
assignee: []
created_date: '2026-06-08 09:31'
updated_date: '2026-07-13 20:16'
labels:
  - e2e
  - smapi
  - notifications
milestone: m-4
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/ProactiveEvents/ProactiveEventService.cs
  - Jellyfin.Plugin.AlexaSkill/ProactiveEvents/ProactiveEventClient.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
ProactiveEventService and ProactiveEventClient send background notifications to Alexa devices. Completely untested — no unit tests, no E2E. Need to:
1. Verify ProactiveEventService starts correctly
2. Test rate limiter behavior (ProactiveEventRateLimiter)
3. Verify notification payload format matches Amazon's schema
4. Test token refresh for notification API calls
5. Verify graceful failure when user hasn't enabled notifications

Depends on: Amazon Proactive Events API, SMAPI auth tokens.
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
