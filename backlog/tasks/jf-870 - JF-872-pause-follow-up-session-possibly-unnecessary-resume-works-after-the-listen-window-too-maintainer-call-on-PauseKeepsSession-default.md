---
id: JF-870
title: >-
  JF-872: pause follow-up session possibly unnecessary (resume works after the
  listen window too) - maintainer call on PauseKeepsSession default
status: To Do
assignee: []
created_date: '2026-10-12 16:02'
labels:
  - 'gate:maintainer-call'
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Maintainer observation from the 2026-10-12 screenless device round (S2/S4): with PauseKeepsSession on (JF-482 experiment, default TRUE), the pause response keeps the session open with the "dimmi pure quando vuoi riprendere" follow-up. Paolo tested resuming BOTH inside the listen window AND after it closed: resume works correctly in both cases (after the window, the plain "Alexa, riprendi"/one-shot path resolves). His product take: the open follow-up may be unnecessary ("quindi forse il followup aperto non serve davvero").

Evidence recorded; the DECISION is Paolo's (gate:maintainer-call): keep PauseKeepsSession default TRUE as-is, flip default to FALSE (resume still works), or keep the flag but shorten the reprompt wording. No code change until he calls it. Related context: JF-299 (event-response restrictions) is unrelated to this audio-pause path; the JF-482 flag is the only owner.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
