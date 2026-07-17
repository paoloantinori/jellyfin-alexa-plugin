---
id: JF-272
title: Verify SetReminder via Alexa Reminders API
status: To Do
assignee: []
created_date: '2026-06-08 09:31'
updated_date: '2026-07-13 20:16'
labels:
  - e2e
  - smapi
milestone: m-4
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/SetReminderIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
SetReminderIntent uses Alexa's Reminders API. No tests exist. Need to:
1. Test reminder creation via voice command
2. Verify reminder fires at correct time
3. Test permission handling (user must grant reminder permission)
4. Verify graceful failure when permissions missing
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
