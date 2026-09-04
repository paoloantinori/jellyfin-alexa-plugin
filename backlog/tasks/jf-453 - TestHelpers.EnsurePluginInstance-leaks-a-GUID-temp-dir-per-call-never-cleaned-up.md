---
id: JF-453
title: >-
  TestHelpers.EnsurePluginInstance leaks a GUID temp dir per call, never cleaned
  up
status: Done
assignee: []
created_date: '2026-09-02 03:32'
updated_date: '2026-09-04 16:00'
labels:
  - tech-debt
  - test-hygiene
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill.Tests/Unit/TestHelpers.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-433 code-review pass (2026-09-02). Tests/Unit/TestHelpers.cs EnsurePluginInstance (lines ~152-153) creates a GUID-suffixed temp dir per first-call and never deletes it; every later call with Plugin.Instance already set syncs only the flag and returns. Tests that reset Plugin.Instance between runs (PluginTestBase) therefore leak one temp dir per test run; the JF-433 DispatchHarness EnableExecution path adds 2 more per run. Shared infra used by many suites, so the fix needs care: register created dirs in a static list and sweep them via a finalizer/xunit assembly-fixture, or reuse a single deterministic dir. LOW value (leaked empty dirs in /tmp), but it compounds across nightly runs.
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
Closed complete (commits 95ff9ce6 with JF-485, evidence below).

The leak was far larger than filed: xUnit news every test class per method and PluginTestBase resets Plugin.Instance, so EnsurePluginInstance minted a fresh GUID dir per test METHOD (roughly 37,800 accumulated in /tmp across 48 families). Fix: PluginTempDirCleanup (ConcurrentBag register + ModuleInitializer/ProcessExit sweep; xUnit 2.7 has no assembly fixture and the collection-fixture workaround silently drifts). Registered-paths-only deletion, never pattern sweeps. Measured: every covered family byte-flat across consecutive full-suite runs; a filtered run's dir peak returns to exactly its baseline after exit, proving the sweep fires. 4 hermetic sweeper pins; suite 3175/3175.

Follow-ups filed from the measurements: JF-485 (the 13 private inline copies across 9 files: +102/run, landed as the same commit's one-line Registers, leak measured to zero) and JF-486 (the residual +9/run from shuffle-test/gapless, suspected delayed queue-flush recreation). The ~38k HISTORICAL dirs remain in /tmp (pattern deletion unsafe while a suite may run; low value, noted in JF-486).
<!-- SECTION:FINAL_SUMMARY:END -->
