---
id: JF-969
title: >-
  Probe: profile-nlu multiTurnToken for second-turn routing tests (may not apply
  to code-driven dialog)
status: Done
assignee: []
created_date: '2026-11-25 16:05'
updated_date: '2026-11-25 16:20'
labels:
  - nlu-tooling
  - probe
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
CLOSED as not-applicable-on-our-model-shape, evidence inside. The Utterance Profiler request body accepts a multiTurnToken (doc-verified 2026-11-25): an opaque token from the previous response's multiTurn.token carrying multi-turn dialog context, with the response carrying multiTurn.dialogAct/targetSlot/prompt. Probe results: (a) empirical, Fable 2026-11-25 ~17:12 — "di suonare una canzone" (it-IT, FindSong opener) selected FindSongIntent with NO multiTurn key in the response; (b) structural — our dialog is code-driven with elicitationRequired:false on EVERY slot in every locale, and per the Amazon doc the multiTurn object appears only when the selected intent has dialog defined and the dialog is NOT complete; with all slots non-required the model dialog always reports complete, so no token is ever minted on our shape. The attempted second empirical point (RateItemIntent with an unfilled star_rating) cannot be run: "rate this song" matches no sample (all RateItem samples require the star_rating slot) and profile-nlu errors on the phrase class. REOPEN condition: if any future locale/model change introduces elicitationRequired:true slots or dialog delegation, re-run the one-call probe then.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
gate-exempt: no code change, probe-only documentation closure
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Probe closed not-applicable: multiTurnToken never mints on our code-driven dialog shape (all slots elicitationRequired:false, dialog always complete). Empirical: FindSong opener shows no multiTurn key (Fable's live probe); structural reasoning documented; reopen condition recorded for any future required-slot/delegation change. No code, probe-only closure.
<!-- SECTION:FINAL_SUMMARY:END -->
