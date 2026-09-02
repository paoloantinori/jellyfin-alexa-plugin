---
id: JF-453
title: >-
  TestHelpers.EnsurePluginInstance leaks a GUID temp dir per call, never cleaned
  up
status: To Do
assignee: []
created_date: '2026-09-02 03:32'
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
