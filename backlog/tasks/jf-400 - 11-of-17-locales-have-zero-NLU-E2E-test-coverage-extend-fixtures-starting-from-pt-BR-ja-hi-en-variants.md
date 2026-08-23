---
id: JF-400
title: >-
  11 of 17 locales have zero NLU/E2E test coverage - extend fixtures starting
  from pt-BR, ja, hi, en-variants
status: To Do
assignee: []
created_date: '2026-08-23 05:57'
labels:
  - testing
  - nlu
  - localization
milestone: m-16
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Maturity finding (2026-08-23 assessment). NLU fixtures exist for only 6 of 17 locales (it-IT 125, en-US 118, de-DE 61, fr-FR 59, en-GB 57, es-ES 54 utterances); E2E effectively covers it-IT only (en-US E2E documented unreliable, competes with built-in skills). 11 locales (including all es-MX/es-US/fr-CA/en-IN/en-CA/en-AU/pt-BR/nl-NL/ar-SA/hi-IN/ja-JP) have zero test coverage: their model quality is unverifiable and regressions from sample changes land silently.

Plan: extend NLU fixtures locale by locale. Not all 11 are equal priority: pt-BR, ja-JP, hi-IN, en-AU/CA/IN (variant English, cheap to derive from en-GB/en-US) first. Each fixture needs the locale's real sample vocabulary cross-referenced against its slot types (anti-pattern #8). This pairs with JF-399 (sample parity): parity work without fixtures is unverifiable.
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
