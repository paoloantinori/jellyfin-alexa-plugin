---
id: JF-903
title: >-
  Truncated-tail bare infinitives ('chiedi a mia collezione di attivare') land
  on ShowMore's pagination refusal instead of a graceful what-do-you-want line
status: Done
assignee: []
created_date: '2026-10-17 14:43'
updated_date: '2026-10-17 18:35'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED by probe evidence 2026-10-17 ~20:3x (profile-nlu on the live saved model, 5 shapes): the 14/10 ShowMore steal is NOT reproducible on the current model. 'chiedi a mia collezione di attivare' -> AMAZON.FallbackIntent (answered by FallbackIntentHandler with the graceful CouldNotUnderstand help line, exactly the class this task asked for), as do the other truncated infinitives ('di mettere', 'di riprodurre'); bare 'attivare' -> LoopSongOnIntent (partial fill of the JF-902 bare-infinitive rows); and the intended complete phrase 'chiedi a mia collezione di attivare il loop' -> LoopSongOnIntent (repeat-one, the JF-901 semantics the user meant that day). INTERPRETATION: the 14/10 incident was a one-off trainer state on the pre-JF-889/JF-902 model (the loop-family rows and their trainer state have changed twice since); the current model already provides the graceful landing and the correct routing for the complete phrase, so the fix condition (a real steal on the current model) is not met. WAKE CONDITION preserved: a second LIVE occurrence (device, not profile-nlu; the two layers diverge, per the 2026-10-17 routing research) reopens the class with this probe protocol already in the record. gate-exempt: doc-only closure on read-only probe evidence; no code, no model change.
<!-- SECTION:FINAL_SUMMARY:END -->
