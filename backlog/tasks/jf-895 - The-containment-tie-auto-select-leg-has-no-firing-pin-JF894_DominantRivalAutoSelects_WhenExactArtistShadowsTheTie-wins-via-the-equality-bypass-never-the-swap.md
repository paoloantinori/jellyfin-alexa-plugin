---
id: JF-895
title: >-
  The containment-tie auto-select leg has no firing pin:
  JF894_DominantRivalAutoSelects_WhenExactArtistShadowsTheTie wins via the
  equality bypass, never the swap
status: To Do
assignee: []
created_date: '2026-10-16 10:02'
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
