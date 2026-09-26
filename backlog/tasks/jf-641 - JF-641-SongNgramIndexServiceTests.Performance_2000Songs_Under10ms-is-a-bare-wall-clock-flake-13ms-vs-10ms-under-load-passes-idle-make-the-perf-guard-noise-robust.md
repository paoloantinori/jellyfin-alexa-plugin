---
id: JF-641
title: >-
  JF-641 - SongNgramIndexServiceTests.Performance_2000Songs_Under10ms is a bare
  wall-clock flake (13ms vs 10ms under load, passes idle) - make the perf guard
  noise-robust
status: To Do
assignee: []
created_date: '2026-09-26 19:48'
labels:
  - tests
  - flaky
  - tech-debt
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-640 verification run (2026-09-26, same-turn rule for failures found but not fixed). During the full-suite run on this workstation, `Jellyfin.Plugin.AlexaSkill.Tests/Unit/SongNgramIndexServiceTests.cs` `Performance_2000Songs_Under10ms` failed ONCE on the net10.0 testhost with "Search took 13ms, expected < 10ms" while the net9.0 run of the same suite passed. Mechanical evidence collected: (1) the failure is load/order dependent - run in isolation right after the 7-minute suite it failed on BOTH TFMs, but passes in isolation and with its whole test class once the machine is idle; (2) the test is a bare wall-clock assertion (Stopwatch around `service.Search`, hard `< 10ms` budget) with no warmup iteration, so a cold-JIT or loaded-machine testhost pays the whole first-call cost inside the measured window; (3) it has zero code-path overlap with the JF-640 diff that surfaced it (PlayPodcastIntentHandler + its tests only). This is the CI-vs-local divergence class (project memory: local pass does not prove CI passes; the CLAUDE.md coverage caveat). The test's intent is an ORDER-OF-MAGNITUDE regression guard (the n-gram index must stay O(1)-ish), not a precise 10ms SLA. Desired outcome: make the guard robust to environment noise while keeping its regression teeth - e.g. measure steady-state after a warmup call, or assert a budget with headroom (10x the median), or retry-once-on-breach; pick the shape that matches how ArtistIndexServiceTests' sibling `< 10ms` perf test handles it (check whether it has the same latent flake and fix both in one shape). Not fixed in the JF-640 turn because loosening a perf budget is a judgment call that belongs to its own reviewed change, not a drive-by inside an unrelated fix.
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
