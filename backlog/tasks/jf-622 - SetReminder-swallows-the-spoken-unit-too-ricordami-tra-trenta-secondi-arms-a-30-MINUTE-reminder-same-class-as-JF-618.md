---
id: JF-622
title: >-
  SetReminder swallows the spoken unit too: "ricordami tra trenta secondi" arms
  a 30-MINUTE reminder (same class as JF-618)
status: To Do
assignee: []
created_date: '2026-09-23 08:08'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-618 -
    Sleep-timer-swallows-the-spoken-unit-fermare-dopo-5-secondi-sets-a-5-MINUTE-timer-duration-slot-is-number-typed-minutes.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Same defect class JF-618 fixed for the sleep timer, found by the JF-618 code review: SetReminderIntent's duration_minutes is a number-typed minutes slot (ItalianNumber/AMAZON.NUMBER per locale), so the spoken unit is swallowed and BuildRelativeReminder hardcodes OffsetInSeconds = minutes * 60; «ricordami tra trenta secondi» arms a 30-MINUTE reminder. Migration path is paved: rename the slot (e.g. reminder_duration), type AMAZON.DURATION, parse via ResumeMath.ParseAlexaDuration (already the shared home), and the spoken confirmation needs the same unit-aware treatment (ResumeMath.FormatSpokenLargestUnit + the ReminderSetRelative string family). Deliberately NOT bundled with JF-618: the reminder feature is still device-unverified (JF-272) and its wording fixtures are trainer-sensitive (the 2026-09-23 es-MX re-probe showed the reminder/timer competition flips across geometry changes).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 «ricordami tra trenta secondi» arms a 30-SECOND reminder (or a deliberate spoken refusal if seconds are out of scope for reminders), not a 30-minute one
- [ ] #2 Minute/hour reminder forms keep working in all 17 locales
- [ ] #3 Models regenerated, fixtures updated, handler parses the duration through ResumeMath.ParseAlexaDuration (the JF-618 shared home)
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
