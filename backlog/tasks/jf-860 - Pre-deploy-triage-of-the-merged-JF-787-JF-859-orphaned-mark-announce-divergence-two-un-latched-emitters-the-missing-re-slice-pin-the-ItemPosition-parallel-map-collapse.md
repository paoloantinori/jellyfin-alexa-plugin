---
id: JF-860
title: >-
  Pre-deploy triage of the merged JF-787/JF-859 (orphaned-mark announce
  divergence, two un-latched emitters, the missing re-slice pin) + the
  ItemPosition parallel-map collapse
status: In Progress
assignee: []
created_date: '2026-10-10 18:41'
updated_date: '2026-10-10 18:41'
labels:
  - tech-debt
  - playback
  - follow-up
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-10 by the orchestrator, TWO same-turn landings from the JF-648 completion round (both barred from the worker's file set):

PART A - the three mis-targeted-review findings on the JUST-MERGED JF-787/JF-859 work (the review's first invocation targeted main's merge commits; its findings are real and the code is NOT yet deployed - fix before exposure):
1. AudiobookPositionTracker.cs ~line 253: the exact-match timeline identity permanently orphans a book's marks after metadata/scope drift; GetPositionTicks still reads the orphaned mark while ResolveServeSliceTicks drops the slice - the announce can say 4h while the book restarts at 0:00 (pre-JF-787 honored the same mark). Decide the read-side null policy consciously (e.g. the announce reading the same gate as the serve, or a documented divergence) with its own pin.
2. VideoAudioController.cs ~3974/3981: the two tracker-gate Information lines are un-latched per-request emitters, reintroducing the refresh-noise class the JF-818 latch quiets on the neighboring line - latch them once per generation (the JF-859 pattern).
3. VideoAudioController.cs ~3978: the gate's re-slice arm has no controller-level pin - add it.

PART B - the JF-648 altitude round's named follow-up (the CopySurvivingStores doc records the JF-637 precedent): ItemPositionState + ItemPositionKinds is now the store's LAST convention-enforced parallel-map pair; the same collapse (Dictionary<string, ItemPositionRecord> with ticks plus nullable kind, null as the DESIGNED kindless state per JF-812) should get its own task. Design-bearing: mind the JF-812 legacy-kindless semantics (null kind = releases) and the JF-694 trim subset contract.
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
