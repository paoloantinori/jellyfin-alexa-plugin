---
id: JF-618
title: >-
  Sleep timer swallows the spoken unit: "fermare dopo 5 secondi" sets a 5-MINUTE
  timer (duration slot is number-typed minutes)
status: In Progress
assignee: []
created_date: '2026-09-22 16:02'
updated_date: '2026-09-23 08:19'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-617 -
    Resume-after-stop-DeviceQueue-fallback-unreachable-when-AudioPlayer-token-and-session-item-are-both-null-so-riprendi-one-shot-launches-stale-server-progress-item.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live incident 2026-09-22 17:37 (battery test 7): user said «fermare dopo 5 secondi»; the SleepTimerIntent slot duration_minutes resolved to bare "5" (the NLU strips the unit word because every sample says "minuti"), and the handler set a 5-MINUTE timer spoken back as "Timer di spegnimento impostato per 5 minuti". Any seconds-form request is silently coerced to minutes today. Root cause is the slot design: a number-typed minutes slot cannot carry the unit. Fix direction: switch the slot to AMAZON.DURATION (resolves spoken durations to ISO 8601: "5 secondi" -> PT5S, "un'ora" -> PT1H) in all 17 locale templates, adapt SleepTimerIntentHandler to parse the ISO duration (XmlConvert or manual PT parse; keep the cancel form working), keep the elicit on unparseable input, update NLU fixtures. Note the sleep deadline is minted via StreamTokenCodec.MintSleepTimerToken(itemGuid, deadlineTicks) and enforced in PlaybackNearlyFinishedEventHandler, which are unit-agnostic (ticks) - only slot parsing and the confirmation string change. The confirmation string "SleepTimerSet" hardcodes "minuti" ({0} minutes) and needs a unit-aware form or separate strings for seconds/minutes/hours in 17 locales.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 «fermare dopo 30 secondi» sets a 30-second stop deadline (slot carries the unit, not a bare number reinterpreted as minutes)
- [x] #2 «fermare dopo un'ora» / «mezz'ora» / «45 minuti» keep working (hour/half-hour/minute forms)
- [x] #3 The elicitation on an unparseable duration still works in all 17 locales
- [ ] #4 Model regenerated in all 17 locales from templates, validator + NLU fixtures updated, deployed and device-verified
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-23 08:10: IMPLEMENTED AND LIVE (deployed to minix, all 17 locale models rebuilt; ja-JP needed the JF-513 slot-space restored after a live build failure). Design: slot renamed duration_minutes -> sleep_duration typed AMAZON.DURATION (SetReminderIntent deliberately KEEPS duration_minutes as ItalianNumber/AMAZON.NUMBER: same slot name must share a type across intents, and the reminder path wants in-N-minutes numbers). Handler: XmlConvert.ToTimeSpan ISO 8601 parse with a bare-number=minutes fallback; exact-Timespan deadline; cancel rides zero duration; confirmation through SleepTimerSetFor + SleepTimerUnit{Second,Minute,Hour}{,s} (7 keys x 17; French minutes==English minutes exempted in the locale-prose test). LIVE VERIFIED profile-nlu: trenta secondi->PT30S (the incident case), cinque minuti->PT5M, mezz'ora->PT30M, un'ora->PT1H; bare infinitives route; the wrapper one-shot prefix fails only as the documented profile-nlu artifact (device battery will confirm). Tests: 4242/4242 x both TFMs. Gates running (simplify 4-angle + code-review high).

2026-09-23 08:20: review round complete, commit 3074d13d pushed, deployed. All 10 code-review findings applied (voice-reference SLOT_HINTS + all mirrors incl. the CI-breaking --check omission; P1W week forms; OverflowException containment; es-MX fixture re-pinned to live routing; stale coupling comment; parse/format moved to ResumeMath as the shared home; SecondsOnly reuse; int domain + reworded fallback comment; French identity pinned to literal; PT59.5S boundary snap) or tracked (the reminder path's identical seconds-swallowing filed as JF-622). AC#4's device verification pending (user battery).
<!-- SECTION:NOTES:END -->

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
