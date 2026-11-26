---
id: JF-975
title: >-
  nlu-lab: surface the JF-936 ERROR-row diagnostics on the human-facing surfaces
  (webapp side projection, app.js error badge/detail, run-ok summary error
  count)
status: To Do
assignee: []
created_date: '2026-11-26 18:50'
labels:
  - nlu-lab
  - diagnostics
  - ux
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-936 simplify/altitude review round (2026-11-26, same-turn landing rule): the ERROR-row diagnostics JF-936 captures now live only inside the run JSON. Every human-facing surface still shows a bare error state with no why: (1) scripts/nlu-lab/webapp.py _side() (lines ~146-150) projects a fixed field set (selected_intent, status, slots, considered_intents) and drops row.error; (2) scripts/nlu-lab/web/app.js renders an error row as a localized "error" badge with no reason text, and the detail card (lines ~190-219) shows no error field; (3) the cli.py run-ok summary (lines ~381-383) prints "run ok: N rows, expectation failures: M" with no error-row count or text, so an all-ERROR run (the expired-auth shape JF-936 cites) reads "run ok" everywhere. Fix: project "error" in _side(), render it in app.js (detail card and/or a title on the err cell), and add an "N error rows" clause to the run-ok summary when N > 0. Small, self-contained; the run JSON schema already carries the field.
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
