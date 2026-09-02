---
id: JF-429
title: >-
  Playback debounce copies (AudiobookPositionTracker, DeviceQueueManager) still
  carry the arm-after-dispose race hardened in
  DebouncedLibraryIndexService.ArmOneShot
status: In Progress
assignee:
  - zai
created_date: '2026-09-01 05:58'
updated_date: '2026-09-02 02:42'
labels:
  - code-review
  - hardening
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Playback/AudiobookPositionTracker.cs:126'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs:482'
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/DebouncedLibraryIndexService.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review finding (2026-09-01, JF-419.3 review round 1 finding 10, CONFIRMED): the codebase has a THIRD family of private resettable one-shot debounce implementations - Alexa/Playback/AudiobookPositionTracker.SchedulePersist (~:126) and Alexa/Playback/DeviceQueueManager.SchedulePersistInternal (~:482), reached from ~10 unguarded call sites. They still carry exactly the arm-after-dispose race the new DebouncedLibraryIndexService.ArmOneShot hardens against (volatile flag set before the lock + in-lock re-check), and they reset via Timer.Change instead of Dispose+new, so the lifecycle semantics have already drifted from the shared shape extracted in JF-419.3. Not fixed there because Playback persistence is a different concern (disk-persist debounce, not hosted index lifecycle); the extraction target is the ARM pattern, not the base class.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Each Playback debounce implementation either adopts DebouncedLibraryIndexService's hardened arm pattern (volatile disposed-flag set before the lock + in-lock re-check + single arm site) or documents why its call sites cannot race
- [ ] #2 The ~10 unguarded SchedulePersist-style call sites are audited: each either routes through the hardened arm or gains the disposed guard
- [ ] #3 Unit test reproduces the arm-after-dispose race for at least one of the two (timer callback invoked after Dispose leaves no pending work)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Simplify round 2026-09-02 (4 angle agents) disposition: applied the comment-precision fix in DeviceQueueManager (in-lock re-check covers Dispose only; Clear is covered by lock mutual exclusion). Skipped as out-of-scope under minimal diff: (a) AudiobookPositionTracker._persistTimers is a single fixed-key ConcurrentDictionary whose every access now sits inside _persistLock; a plain Timer? field would be strictly simpler and mirror the reference shape. Deferred cleanup, fold in only if the tracker is touched again. (b) Internal test hooks (FailedLoadRetryInterval style) on both debounce constants would cut the two new race tests' 7s of Thread.Sleep to ~100ms; rejected to avoid expanding API surface for tests only.
<!-- SECTION:NOTES:END -->

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
