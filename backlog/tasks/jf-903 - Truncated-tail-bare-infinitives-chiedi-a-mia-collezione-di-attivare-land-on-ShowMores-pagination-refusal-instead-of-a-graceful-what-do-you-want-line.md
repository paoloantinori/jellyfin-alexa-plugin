---
id: JF-903
title: >-
  Truncated-tail bare infinitives ('chiedi a mia collezione di attivare') land
  on ShowMore's pagination refusal instead of a graceful what-do-you-want line
status: To Do
assignee: []
created_date: '2026-10-17 14:43'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-886 transcription resolution (hassio RVH read, 2026-10-17): the 2026-10-14 17:16 incident was 'chiedi a mia collezione di attivare' - a bare infinitive with its tail cut by ASR (likely 'il loop' lost) - and it resolved to ShowMoreIntent ('Non ho una lista precedente da continuare'), a nonsensical answer for a bare activation verb. CLASS: truncated-tail one-shots; when ASR cuts an object-dependent verb ('attivare', 'metti', 'riproduci') the skill should answer a graceful help/what-do-you-want line, not a pagination-continuation refusal. INVESTIGATE: what ShowMore samples capture a bare 'attivare' (the continua/ancora family must be absorbing it somehow - check profile-nlu for 'attivare', 'chiedi a mia collezione di attivare', and a few more truncated verbs); if the steal is real, the fix is a carrier/wording adjustment on the ShowMore family or a FallbackIntent line for bare infinitives, per the anti-pattern #3 discipline (no greedy bare patterns). LOW priority: the shape needs a second live occurrence to justify model churn (n=1 so far, and ASR truncation is rare); document with a live probe first.
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
