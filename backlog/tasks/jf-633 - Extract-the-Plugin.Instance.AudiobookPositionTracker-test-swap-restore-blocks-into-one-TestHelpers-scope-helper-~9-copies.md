---
id: JF-633
title: >-
  Extract the Plugin.Instance.AudiobookPositionTracker test swap/restore blocks
  into one TestHelpers scope helper (~9 copies)
status: Done
assignee: []
created_date: '2026-09-25 05:23'
updated_date: '2026-09-25 13:01'
labels:
  - tech-debt
  - tests
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill.Tests/Unit/TestHelpers.cs
  - >-
    Jellyfin.Plugin.AlexaSkill.Tests/Handler/ResumeIntentHandlerServerProgressTests.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/StartOverIntentHandlerTests.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-630 /simplify pass (2026-09-25, same-turn filing rule), the sibling of JF-630 itself.

JF-630 gave DeviceQueueManager swaps one scope helper (TestHelpers.SwapPluginQueueManager). The same assign/teardown pattern for Plugin.Instance.AudiobookPositionTracker still exists as ~9-10 hand-rolled method-level copies: create tracker via TestHelpers.CreatePositionTracker, assign to Plugin.Instance, run test, finally { Plugin.Instance.AudiobookPositionTracker = null; tracker.Dispose(); }. Sites (assign/restore line pairs):

- Jellyfin.Plugin.AlexaSkill.Tests/Handler/ResumeIntentHandlerServerProgressTests.cs:561/622, 752/799, 866/921, 937/978 (four sites)
- Jellyfin.Plugin.AlexaSkill.Tests/Handler/StartOverIntentHandlerTests.cs:502/572
- Jellyfin.Plugin.AlexaSkill.Tests/Handler/ResumeMathTests.cs:149/162
- Jellyfin.Plugin.AlexaSkill.Tests/Handler/PlayBookIntentHandlerTests.cs:351/399
- Jellyfin.Plugin.AlexaSkill.Tests/Unit/AlbumAnnounceVehicleTests.cs:179/196
- Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs:4154/4167, 4196/4208

Two sites sit inside the very methods JF-630 converted (the tracker teardown beside the new ledger swap scope), which is the asymmetry that surfaced this.

Extraction: add TestHelpers.SwapPluginPositionTracker(AudiobookPositionTracker) mirroring SwapPluginQueueManager (capture previous, assign, restore BEFORE dispose, scope owns the tracker's disposal; CreatePositionTracker's doc says wiring + dispose-in-finally is the caller's job, so update that doc the same way the queue factory pair reads now).

Judgment items before converting:
1. These sites restore to null, not to a captured previous value. JF-630 judged restore-previous equivalent for DeviceQueueManager (PluginTestBase resets Instance per class; only SkillStartup assigns it, and SkillStartup never runs in the test host). VERIFY the same argument holds for AudiobookPositionTracker (grep production assignments of Plugin.Instance.AudiobookPositionTracker); if some site relies on the hard null under a shared instance, convert that site to the explicit form instead.
2. Re-grep the census before starting; new sites may have appeared since 2026-09-25 (grep -rn "AudiobookPositionTracker = " across the test project; include any site not in the list above, and grep DeviceQueueManager = at the same time to confirm the JF-630 TvNextUp straggler is gone).

Mechanical change; full suite verifies.
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
2026-09-25 JF-630 code-review additions to this plan: (a) the extraction must use ONE shared private core (capture/assign/restore-before-dispose) with two thin named wrappers (SwapPluginQueueManager + SwapPluginPositionTracker), so the load-bearing ordering invariant lives in exactly one place instead of two hand-copied scope classes; (b) the two method-level JF-630 conversion sites (ResumeIntentHandlerServerProgressTests ~563, StartOverIntentHandlerTests ~504) split one test's teardown across the explicit finally (tracker + config flag) and the using-at-exit (manager) - when this task lands a tracker scope, let those usings chain ALL resource teardown and drop the split; also the new scope should use the loud Plugin.Instance! guards (the JF-630 review's verdict on silent no-op swaps).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-633 complete: ONE swap core for both plugin test-swap families. PluginInstanceSwap<T> (capture/assign/restore-before-dispose, loud guards) with the thin wrappers SwapPluginQueueManager and SwapPluginPositionTracker; the census re-verified (10 tracker sites converted, no new sites, the JF-527 harness documented as the sanctioned exception beside the core) and the production-assignment equivalence proven (SkillStartup is the only assigner, never runs in the test host). The review hardened the core: the null-previous assumption is now an enforced assertion (a foreign assignment fails loudly at the next swap instead of silently re-installing disposed state), Dispose survives a throwing restore, the two chained sites dispose in the old literal order with the comment saying it is deliberate, and CreatePositionTracker's doc states its own disposal rule. Test-only; suite count stayed exactly 4334x2. Gates: /simplify (clean, one micro-nit dropped) + code-review high (5 findings, all applied) in the orchestrator transcript. PUSHED.
<!-- SECTION:FINAL_SUMMARY:END -->
