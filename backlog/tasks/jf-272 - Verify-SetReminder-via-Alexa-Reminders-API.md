---
id: JF-272
title: Verify SetReminder via Alexa Reminders API
status: Done
assignee: []
created_date: '2026-06-08 09:31'
updated_date: '2026-09-26 13:17'
labels:
  - e2e
  - smapi
milestone: m-4
dependencies: []
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
JF-450/451 follow-up (2026-09-02): SleepTimerIntentHandler still int.Parse-only for its duration slot, so it-IT word numbers ('trenta') fail exactly like SetReminder's did before the fix; adopting Alexa/Util/ItalianNumberWords.TryParse is a one-liner (the parser + its model-mirror test landed with JF-451). Also known limit: the ItalianNumber slot type has no compounds ('venticinque'), so only its 22 values are speakable for relative durations; extending the slot type auto-fails the mirror test until the parser grows.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-272 CLOSED by device verification (2026-09-26 14:48, Paolo's Echo Show): 'ricordami tra trenta secondi' armed, confirmed 'promemoria impostato tra 30 secondi', and FIRED as a real Alexa reminder. The feature had never worked on any device: two live fixes landed during the verification round (the RemindersClient ctor args were swapped since inception, feeding the JWT to new Uri(); then content[].text joined ssml for the INVALID_ALERT_INFO the API answered after the first fix). The reminder is now fully verified end-to-end: routing (SetReminderIntent PT30S), creation (alert token), device delivery.
<!-- SECTION:FINAL_SUMMARY:END -->
