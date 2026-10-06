---
id: JF-800
title: >-
  JF-800 - the parallel-phase contract needs a mechanical guard (IL roster test:
  uncollected classes must be static-free)
status: To Do
assignee: []
created_date: '2026-10-06 18:18'
labels:
  - test-infrastructure
  - hardening
dependencies: []
references:
  - >-
    backlog/tasks/jf-792 -
    VideoAudioControllerTests-regrew-to-roughly-two-thirds-of-suite-wall-clock-after-the-windowing-waves-re-evaluate-the-declined-partition-lever.md
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/WarmingGateCoverageTests.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/IlCallScanner.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/PluginCollection.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed by the JF-792 worker (2026-10-06) from the /simplify altitude review round: the JF-792 toggle replaced the assembly-level DisableTestParallelization (a mechanical, per-class-free guarantee) with a PROSE contract in PluginCollection.cs: "classes without a collection attribute must touch none of those statics". That contract covers ~111 uncollected classes and is enforced only by a comment; the repo has documented history of exactly this contract class going stale (WarmingGateCoverageTests exists in JF-465 because the CLAUDE.md gated-handler list and a comment had both rotted), and the JF-792 audit itself found the contract pre-violated on arrival (DeadMicSweepElicitTests inherited PluginTestBase uncollected).

The guard the repo's own machinery already supports: a roster test in the WarmingGateCoverageTests idiom, reusing the shared IlCallScanner (Handler/IlCallScanner.cs, JF-582, already serving a second consumer). For every test-assembly class WITHOUT [Collection("Plugin")]: assert (a) it is not PluginTestBase-assignable (that one reflection check catches the actual historical DeadMic drift shape), and (b) none of its own method bodies reference the known shared static surface (Plugin.get_Instance / Plugin.ResetInstance, QueueContinuationStore, RadioModeState, PlaybackReportOrdering, StreamTokenHelper minting, the VideoAudioController encode statics UpdateEncodeGateCapacity / LiveSpeedEncodeCacheKeysForTest / EncodeGateConfiguredCapacityForTest). Transitive reads through PRODUCTION code (e.g. the VideoAudioController ctor's null-tolerant Plugin.Instance read at VideoAudioController.cs:239) are deliberately outside the scan; VideoAudioControllerPureTests' doc licenses them. Expected roster: the Plugin collection members plus TestHelpers themselves may need an allowlist entry.

Secondary payload, gated on the guard landing: DeadMicSweepElicitTests can then be reconsidered for de-basing (its PluginTestBase inheritance is provably unused ceremony per its own doc: the elicit branches return before any dependency is touched), which would return those 12 tests to the parallel phase; with the guard in place a future violation reds mechanically instead of silently.

Why filed rather than done in JF-792: JF-792's constraint set was arrange-only with zero new assertions; a roster test is new assertion machinery and deserved its own scope (the altitude reviewer's own proportionality verdict).
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
