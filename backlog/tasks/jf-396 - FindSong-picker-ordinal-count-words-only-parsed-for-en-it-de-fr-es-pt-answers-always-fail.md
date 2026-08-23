---
id: JF-396
title: >-
  FindSong picker ordinal/count words only parsed for en/it/de/fr - es/pt
  answers always fail
status: To Do
assignee: []
created_date: '2026-08-23 05:56'
labels:
  - localization
  - multi-turn
  - findsong
milestone: m-15
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Localization gap (found by the 2026-08-23 multi-turn audit). FindSongIntentHandler.GetOrdinalWord/TryParseNumericPick and the ordinal-substring checks cover English, Italian, German, French only. Spanish ("dos", "cuatro", "segundo", "tercero", "cuarto"), Portuguese ("segundo", "quarto", "dois", "quatro") and the other locales are not matched: an es-*/pt-BR user answering the candidate picker with a spoken ordinal or count-word always falls into FindSongInvalidPick. The prompts are localized in all 17 locales but the answer parser is not: split the ordinal/count word tables per locale (or drive them from the locale files).
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
