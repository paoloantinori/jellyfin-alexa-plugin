---
id: JF-630
title: >-
  Extract the Plugin.Instance.DeviceQueueManager test swap/restore block into
  one TestHelpers scope helper (4 copies)
status: Done
assignee: []
created_date: '2026-09-25 02:34'
updated_date: '2026-09-25 05:52'
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
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
/simplify REUSE+ALTITUDE pass (2026-09-25, over commit 0b31ac7e): one census miss found. TvNextUpServiceTests.cs:404-434 (PlayNextUpAsync) still hand-rolls the exact swap shape: capture `previous`, guarded assign, finally restore-then-dispose. It is conditional (only when queueSeeding != null) but mechanically convertible: `using IDisposable? swap = queue != null ? TestHelpers.SwapPluginQueueManager(queue) : null;` (using-on-null is a no-op, preserving the shape). Convert it in this task so the helper doc's 'the ONE swap scope' claim is true; grep `DeviceQueueManager = ` to confirm zero hand-rolled copies remain.

Examined and correctly NOT a conversion site: EventHandlerTests.cs:1070 (RecordPreviousPlayOnHarnessDevice, JF-527) assigns and deliberately disposes the manager in place WITHOUT restore (disposed-manager-still-attached is the point; reads hit the in-memory queue). The swap scope would change its semantics; leave it.

Optional nit from the same pass: the scope's `Plugin.Instance != null` guards are dead at all seven sites (EnsurePluginInstance runs before every swap; the method-level sites deref Plugin.Instance!.Configuration first). A future mis-ordered call would silently skip the swap yet still dispose the manager; `Plugin.Instance!` would fail loudly at the swap line (house style on silent failures). Pre-existing defensive shape carried over; not blocking.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-630 complete: TestHelpers.SwapPluginQueueManager is the ONE swap scope (capture+assign+restore-before-dispose, loud Plugin.Instance! guards). Seven conversions: four suite-level blocks, two method-level swap-to-null pairs (equivalence proven constructively), and the TvNextUp conditional straggler the simplify census caught (restructured leak-proof by the review: the scope is acquired before the seed and disposed on a seed throw). No production code; suite count stayed exactly 4318x2 throughout. Gates: /simplify (2 combined agents) and code-review high (6 findings: 4 applied, 2 folded into JF-633's plan) both in this transcript; JF-633 filed for the AudiobookPositionTracker sibling family with the shared-core requirement.
<!-- SECTION:FINAL_SUMMARY:END -->
