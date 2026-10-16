---
id: JF-895
title: >-
  The containment-tie auto-select leg has no firing pin:
  JF894_DominantRivalAutoSelects_WhenExactArtistShadowsTheTie wins via the
  equality bypass, never the swap
status: Done
assignee: []
created_date: '2026-10-16 10:02'
updated_date: '2026-10-16 11:18'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-892 code-review round (worker handback, 2026-10-16): the pin JF894_DominantRivalAutoSelects_WhenExactArtistShadowsTheTie never exercises the ResolveContainmentTie auto-select swap it was named for. On its index-backed fixture the phonetic overload only early-exits above 91, so the exact 'Jones' won the single-best collapse at 100 outright and the JF-420.1 equality bypass answered; the auto-select leg has no genuine fixture (pre-existing miss, unchanged by JF-892's diff). After JF-892 the leg's natural trigger (an exact-rank1 winning the collapse over a non-exact rival) is increasingly handled upstream by exact-first ordering, so the leg needs either a fixture whose collapse winner is a NON-exact dominant rival over a scored rival (forcing the swap to fire), or a deliberate, documented trigger-set shrink of ResolveContainmentTie's auto-select arm. PIN WHEN TAKEN: a fixture where the auto-select swap demonstrably fires (assert the branch's Information log line or the swap's outcome), or the shrink documented at the method.
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
EXECUTED on branch jf895 (4ebb83c2), merged to main f822cc8a, pushed. DELIVERABLE (A): the auto-select swap arm has a genuine firing pin. JF895_DominantRivalAutoSelects_NonExactWinner_SwapFires uses the rival-pool/chain-results asymmetry (tier-1 Contains filter vs the full scoped index fetch): query 'jones', chain [Danko Jones, Norah Jones] with Danko winning the collapse at fair 40.9 (non-exact, no bypass), while pool-only 'Jonas' clears both AlternativeFullNameThreshold (80) and the 20-point margin, so ResolveContainmentTie swaps and auto-plays Jonas (binary outcome via songs-fetch keyed to Jonas's id). TWIN JF895_Twin_WeakRivalBelowTheFairBar_FallsBackToTheAsk uses 'Jonasine' (8-char name caps fair at <=62.5, below the bar whatever the raw score) to prove the same machinery falls to the JF-420.2 ask: swap fires in one, ask answers in the other. The old JF-894 pin renamed to JF894_ExactNamedArtistWinsCollapse_PlaysThroughTheBypass with a corrected mechanism comment (JF-892's exact-first reorder wins the collapse; the JF-420.1 bypass answers, no swap). Test-only: EntityFallbackTests.cs, +90/-5, no production code. Gates (in 4ebb83c2): 0-warning build both TFMs, 6033/6033 both TFMs, /simplify 4 agents (4 comment findings applied), code-review high 6 findings (4 applied including the Jonasine speech assert that goes red on scorer drift; 2 dispositioned with reasons). Residuals noted in the pin's comment (the bypass mechanism is pinned indirectly; pool-only rivals surfacing in a future chain keeps observable behavior identical). gate-exempt: gates ran in the dispatched worker's transcript; commit 4ebb83c2 carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->
