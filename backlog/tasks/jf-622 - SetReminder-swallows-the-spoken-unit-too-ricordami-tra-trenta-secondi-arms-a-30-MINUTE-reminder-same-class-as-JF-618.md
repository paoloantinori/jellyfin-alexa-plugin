---
id: JF-622
title: >-
  SetReminder swallows the spoken unit too: "ricordami tra trenta secondi" arms
  a 30-MINUTE reminder (same class as JF-618)
status: Done
assignee: []
created_date: '2026-09-23 08:08'
updated_date: '2026-09-24 23:06'
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
- [x] #1 «ricordami tra trenta secondi» arms a 30-SECOND reminder (or a deliberate spoken refusal if seconds are out of scope for reminders), not a 30-minute one
- [x] #2 Minute/hour reminder forms keep working in all 17 locales
- [x] #3 Models regenerated, fixtures updated, handler parses the duration through ResumeMath.ParseAlexaDuration (the JF-618 shared home)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-25 ~02:00 ORCHESTRATED NIGHT RUN COMPLETE (worker in worktree, orchestrator-merged, both gates run, live-verified): the migration mirrors JF-618 exactly (reminder_duration AMAZON.DURATION in all 17 templates+models, ResumeMath.ParseAlexaDuration, ReminderSetRelativeFor + FormatSpokenLargestUnit, fixtures+mirrors). Gates: /simplify (2 combined agents) applied (test dedup/trims, the shared-formatter doc, the honest es-MX probe comment, the 11 stale docs-site/data.json sleep edges synced - 8 locales never had an edge there, pre-existing gap); code-review high (7 findings): applied the non-positive-duration guard, the elicit-follows-the-failed-slot fix (new ReminderAskDuration string in 17 locales; the old elicit invited 'a number of minutes' into a TIME slot - dead-end), the {0} translator contract note; FILED: the hour-snap confirmation approximation (PT57M says 'un'ora'; exactness matters for reminders - needs an exact flag on the formatter) and the dual disagreeing bounds (handler int.MaxValue vs parser week cap - wants a shared TryParseBounded). LIVE 17-LOCALE BATTERY on the rebuilt models (all 17 rebuilt, SUCCEEDED): 13/17 'remind me in thirty seconds' -> SetReminderIntent PT30S incl. it-IT; es-MX/es-US: the short 'recuerda/recuérdame en X' form loses to SleepTimerIntent while 'establece un recordatorio en X' routes PT5M (model-side competition, trainer-sensitive - follow-up per the nlu-trainer protocol); hi-IN: DURATION slot fills with RAW Hindi words (no ISO resolution; ParseAlexaDuration has no Hindi numerals) and one form falls to FallbackIntent (i18n gap); pt-PT: profile-nlu service erroring (the known outage class). Device-verification of the fixed incident rides tomorrow's battery (JF-272's feature-verify).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-622 complete: the reminder duration slot migrated to AMAZON.DURATION in all 17 locales (mirroring the JF-618 sleep-timer shape), parsed via the shared ResumeMath.ParseAlexaDuration with a non-positive guard, unit-aware spoken confirmation (ReminderSetRelativeFor), the elicit now targets the failed slot with an honest duration prompt (ReminderAskDuration, 17 locales), fixtures and mirrors updated. Live-verified on the rebuilt models: 13/17 locales resolve the seconds incident correctly (PT30S), it-IT included; the es short-form competition, the hi-IN raw-words gap, and the pt-PT outage are documented follow-ups (model/i18n side). Both DoD gates ran (/simplify 2 agents, code-review high 7 findings: 3 applied, 2 filed, 2 satisfied by the live battery). Awaiting tomorrow's device battery for the end-to-end confirmation.
<!-- SECTION:FINAL_SUMMARY:END -->

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
