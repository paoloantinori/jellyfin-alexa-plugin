---
id: JF-932
title: >-
  Same-item displacement concurrency stress (issue #45): mint uniqueness,
  active-flag lifecycle, report-ordering wedges
status: In Progress
assignee: []
created_date: '2026-11-20 12:20'
labels: []
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/45'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Maintainer ask (2026-11-20, issue #45 follow-up): stress the same-item displacement machinery programmatically for hidden concurrency problems, since the device side cannot be simulated. Scope: a test-only hammer over the REAL in-memory state that the displacement path shares: PlaybackLaunchBuilder.MintStreamToken (the Interlocked generation counter), DeviceQueueManager's ConcurrentDictionary active-audio flag (JF-655, MarkAudioPlaybackStarted/Stopped/IsAudioPlaybackActive), and PlaybackReportOrdering (BeginStart/ClassifyStop, the JF-447 ordering state; static, so tests use unique deviceIds for isolation). The reporter's fail/work alternation depends on this state machine staying coherent (flag OFF after a failed suffixed play is what makes the next launch bare); a wedge or lost update under interleaving would be a plausible plugin-side contributor. Also includes a live hammer of the minix Simulator endpoint with concurrent same-artist requests to confirm the production chain does not throw or wedge under load (no mint there: the simulator device's flag is cold, which is itself documented by the run). This is also the FIRST direct MintStreamToken test coverage (today it is covered only transitively).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Parallel same-item displacement mints: every concurrent MintStreamToken result on an active same-item stream carries a launch suffix and all generations are distinct
- [ ] #2 Flag-flapping storm: under concurrent MarkStarted/MarkStopped/mint interleaving every minted token is well-formed (bare or unique-suffixed, codec-parseable) and a final MarkStopped leaves IsAudioPlaybackActive false (no stuck-on flag)
- [ ] #3 Context-arm stress: PLAYING activity without the flag mints suffixed tokens with unique generations under concurrency
- [ ] #4 PlaybackReportOrdering survives interleaved BeginStart/ClassifyStop storms with no exception and classifies deterministically after quiesce (Real for the current start, DisplacedSameItem for the superseded same-item token)
- [ ] #5 Full suite green on both TFMs without --no-build
<!-- AC:END -->

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
