---
id: JF-485
title: >-
  Register the 8 private inline GUID-temp-dir copies with PluginTempDirCleanup
  (~102 leaked dirs per suite run remain after JF-453)
status: Done
assignee: []
created_date: '2026-09-04 15:44'
updated_date: '2026-09-04 16:00'
labels: []
dependencies: []
references:
  - JF-453 (the sweeper this extends)
  - Jellyfin.Plugin.AlexaSkill.Tests/PluginTempDirCleanup.cs
  - the 8 leaking files listed in the description
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up from the JF-453 implementation (2026-09-04): the register-and-sweep now covers every dir minted through TestHelpers.EnsurePluginInstance (measured flat across suite runs: the 48 covered suffixes are byte-stable, e.g. findsong-test 5782, playalbum-tests 2248), but 8 test files carry PRIVATE INLINE COPIES of the same GUID-temp-dir block that bypass the shared helper and still leak ~102 dirs per full-suite run:

FeatureFlagTests.cs (4 copies), PipelineTests.cs, UserTests.cs, AudioDeviceCapabilityInterceptorTests.cs, ContentAccessTests.cs (2 copies), PlaybackPreferenceTests.cs, InvocationNameDefaultsTests.cs (jf300-N format), CoverArtTests.cs, VoiceIdentificationTests.cs.

Each is a one-line fix: add PluginTempDirCleanup.Shared.Register(tmpDir) right after the Directory.CreateDirectory call (or route the copy through EnsurePluginInstance where the shape allows it). The sweeper (PluginTempDirCleanup, ModuleInitializer + ProcessExit) already exists from JF-453; no new infrastructure needed.

Optional second step (only after the 8 are fixed): the ~38,000 HISTORICAL GUID-tailed dirs in /tmp predate the sweep. Mass pattern deletion is unsafe while a suite may be running; a safe cleanup is a one-shot script run when no test process is alive (check for running testhost/dotnet-test processes first). Low value: they are empty dirs.
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
Closed complete (commit 95ff9ce6 with JF-453). All 13 private inline GUID-temp-dir sites across the 9 listed files carry the one-line PluginTempDirCleanup.Shared.Register right after their CreateDirectory, matching the shared helper's shape. The Register line was chosen over routing through EnsurePluginInstance because the copies are not uniformly equivalent (several load config differently, one returns the Plugin instance, one sets a trailing-slash address the helper would override; per-file rationale in the worker report). Measured: the ~102-per-run leak from these families is now zero (dashed-GUID delta 0, jf300 flat across runs; run-2 per-family attribution proved run-1's +9 residual is entirely out-of-scope families, filed as JF-486). Suite 3175/3175 twice, Release 0 warnings.
<!-- SECTION:FINAL_SUMMARY:END -->
