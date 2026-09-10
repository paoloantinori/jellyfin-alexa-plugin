---
id: JF-540
title: >-
  TestHelpers.CreateDeviceQueueManager factory + the remaining unregistered GUID
  temp dirs (the JF-453/486 family's structural close)
status: Done
assignee: []
created_date: '2026-09-10 18:05'
updated_date: '2026-09-10 20:16'
labels:
  - tech-debt
  - tests
  - hygiene
dependencies: []
references:
  - JF-535
  - JF-535b
  - JF-486
  - JF-453
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-535b combined gate (2026-09-10), findings 4 and 5 (the two tracker-bound items):

1. TestHelpers.CreateDeviceQueueManager factory: the JF-453/485/486/535/535b family is now FIVE commits strong, each hand-applying the same construction+comment. A factory owning creation (registered temp dir via CreateRegisteredTempDir), disposal wiring, and the rationale comment ONCE makes the bare-shared-root shape unrepresentable for future fixtures. Fold the six JF-535b sites onto it when built.

2. Remaining unregistered GUID temp dirs contradicting TestHelpers' documented MUST ('Test code minting GUID temp dirs MUST go through this helper'): DispatchHarness.cs:58, FollowMeIntentHandlerTests.cs:53, PauseResumeStateTests.cs:42, LastPlayedResponseInterceptorTests.cs:37, ResumeIntentAudioVariantOffsetTests.cs:48, DeviceQueueManagerTests.cs:26, ResumeConfirmationTranscodeBaseTests.cs:54, SkillStartupTests.cs:46/:49. These leak only on aborted hosts (clean runs Dispose), overlapping the JF-453 leak population already tracked in memory (jf453_temp_dir_sweep.md); fold this list into that sweep when taken.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Altitude-angle correction (2026-09-10, the JF-535 range fanout): the fold list for item 1 is SEVEN sites, not six - EventHandlerTests.cs:1071 (RecordPreviousPlayOnHarnessDevice, suffix jf527-dq) is a seventh registered-and-disposed construction in the same family. Addendum to the SmapiRefreshToken note: TokenRefreshTask.cs:77 carries the same IsNullOrEmpty shape as DiagnosticsController.cs:55; cover both when that adjacent item is ever taken.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed 2026-09-10 with merge 66bfcd64 (tests-only; no deploy needed). The family's structural close: TestHelpers.CreateDeviceQueueManager(nameSuffix, logger = null) owns the registered dir + NullLogger default + the cross-load rationale ONCE; 7 registered sites folded (six JF-535b fixtures + RecordPreviousPlayOnHarnessDevice with its Plugin.Instance wiring kept); the 8 filed unregistered mints migrated (factory where DQM-shaped, direct CreateRegisteredTempDir where the dir handle was still needed for finally-deletes or shared-dir managers); the worker's sweep surfaced 4 MORE unregistered fixture mints (PreEnqueueOnStart, VideoAudioControllerTests, AudiobookPositionTracker, VideoAudioCache), folded by the orchestrator in the same change. Post-census: the only raw mints left are PluginTempDirSweeperTests' own (deliberate). Gates: combined simplify+code-review gate (construction semantics preserved at every fold, disposal belt intact, shared-dir/restart-read sites correctly left direct; both its findings applied - redundant qualifications + dead CreateDirectory lines), suites 3582/3582 net9.0 (worker + orchestrator + gate), 0 warnings both TFMs.
<!-- SECTION:FINAL_SUMMARY:END -->
