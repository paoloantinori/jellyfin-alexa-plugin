---
id: JF-630
title: >-
  Extract the Plugin.Instance.DeviceQueueManager test swap/restore block into
  one TestHelpers scope helper (4 copies)
status: To Do
assignee: []
created_date: '2026-09-25 02:34'
labels:
  - tech-debt
  - tests
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/PlaybackPositionProvenanceTests.cs
  - >-
    Jellyfin.Plugin.AlexaSkill.Tests/Handler/ResumeConfirmationTranscodeBaseTests.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/ResumeSeedFallbackTests.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/SleepTimerIntentHandlerTests.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Unit/TestHelpers.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-628 /simplify pass (2026-09-25, reuse finding 2, same-turn filing rule).

The save/swap/restore block that points Plugin.Instance.DeviceQueueManager at a suite-local DeviceQueueManager (capture _previousPluginQueueManager, guarded reassignment, restore + Dispose) now exists as FOUR near-identical copies: PlaybackPositionProvenanceTests.cs:61-77, ResumeConfirmationTranscodeBaseTests.cs:66-78, ResumeSeedFallbackTests.cs:53-67, and SleepTimerIntentHandlerTests.cs (added by JF-628, which faithfully mirrored the pattern). Two cruder swap-to-null variants also exist (ResumeIntentHandlerServerProgressTests.cs:563/624, StartOverIntentHandlerTests.cs:504/574). The repo convention (TestHelpers "ONE factory" docs; helpers like CreateSong/AssertNoAudioPlayDirective extracted at 2-3 copies) says an IDisposable scope helper on TestHelpers (e.g. SwapPluginQueueManager(manager) owning create+swap+restore+dispose) has crossed its extraction threshold. JF-628 deliberately did NOT do the extraction (no drive-by refactor across unrelated suites in a fix task); this task owns it: add the helper and convert all four sites (judge the two swap-to-null variants for inclusion). Mechanical change; full suite verifies.
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
