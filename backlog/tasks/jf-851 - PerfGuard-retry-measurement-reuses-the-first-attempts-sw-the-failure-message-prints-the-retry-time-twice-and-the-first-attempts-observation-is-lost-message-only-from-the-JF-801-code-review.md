---
id: JF-851
title: >-
  PerfGuard retry measurement reuses the first attempt's sw: the failure message
  prints the retry time twice and the first attempt's observation is lost
  (message-only, from the JF-801 code review)
status: To Do
assignee: []
created_date: '2026-10-09 21:11'
labels:
  - diagnostics
  - tech-debt
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-09 by the orchestrator from the JF-801 /code-review high round (finding F4, "unfiled review observation needing a tracker home"; message-only defect, no behavior impact).

PerfGuard.cs (~line 42, the JF-641 helper): the retry measurement reuses the same `sw` variable used for the first attempt, so the failure message prints the RETRY elapsed twice and the first attempt's observation is unrecoverable from the log. Triage consequence: when a PerfGuard violation fires, you cannot tell whether the first attempt or the retry blew the budget. Fix shape: keep two measurements (first-attempt stopwatch + retry stopwatch) and print both explicitly in the failure message. Tiny, message-only; find the file with `grep -rn "class PerfGuard" --include=*.cs`.
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
