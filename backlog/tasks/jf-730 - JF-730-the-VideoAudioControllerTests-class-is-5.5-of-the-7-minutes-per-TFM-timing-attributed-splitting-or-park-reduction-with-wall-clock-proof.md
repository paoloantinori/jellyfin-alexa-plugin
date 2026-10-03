---
id: JF-730
title: >-
  JF-730 - the VideoAudioControllerTests class is ~5.5 of the ~7 minutes per TFM;
  timing-attributed splitting or park-window reduction with wall-clock proof
status: To Do
assignee: []
created_date: '2026-10-03 07:45'
labels:
  - test-infrastructure
  - performance
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 from the workflow-speedup analysis (Paolo's directive). The full suite
takes ~6-7 min per TFM, and the VideoAudioControllerTests class alone is ~5.5 of it
(measured across tonight's runs: the class filter runs ~5m50s per TFM against ~7m00s for
the whole suite). The class's cost is wall-clock, not CPU: hundreds of 400ms park
assertions and the settle-budget awaits (20s worst case) serialize inside ONE xUnit
collection, pinned there by the shared statics (Plugin.Instance, the encode registries,
the cache root) its tests mutate - naive parallelization into multiple collections
would race those statics.

THE WORK, in order:
1. TIMING ATTRIBUTION first: instrument or reason where the 5.5 min goes (the park
   count x 400ms; the settle-budget tests; the encode-fake I/O). Do not guess - count
   the parks (grep Task.Delay(400) and the park assert call sites) and multiply.
2. Evaluate the safe levers with before/after wall-clock proof on both TFMs:
   (a) PARK-WINDOW REDUCTION: the 400ms window exists to let the endpoint park on the
   real per-item gate before the assert; measure whether 150-250ms holds the same
   guarantee on this hardware (the parks are already timing-tolerant asserts, not
   exact sleeps); every halving of the park window halves the class's dominant term.
   (b) SETTLE-BUDGET SEAMS: the 20s EndpointSettleBudget is a worst-case bound most
   tests never reach; a test-seam override (smaller budget in tests that do not
   specifically pin the budget behavior) removes the tail.
   (c) SPLIT-BY-STATIC-OWNERSHIP: if (a)+(b) are insufficient, partition the class
   into collections grouped by WHICH statics they touch (encode-gate family vs cache
   family vs position-tracker family) so within-group serialization is preserved but
   the groups run in parallel; requires an audit of cross-family static touching.
3. Pin nothing new unless a lever changes semantics (it must not); the acceptance is
   the wall-clock delta stated in the task file and the full suite green both TFMs.

CONSTRAINTS: do not weaken any timing assertion's guarantee (the parks prove real
concurrency against real ffmpeg); the JF-677/JF-680/JF-681/JF-700/JF-704 pin families
stay green UNCHANGED; CI equivalence (the class's behavior must not become
hardware-flaky - if a reduced window is adopted, state the measured margin).
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
