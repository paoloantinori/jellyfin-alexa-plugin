---
id: JF-486
title: >-
  Residual +9 leaked temp dirs per suite run: shuffle-test (7) and gapless-dq/rs
  (2), suspected delayed queue-flush recreation after test deletion
status: Done
assignee: []
created_date: '2026-09-04 15:58'
updated_date: '2026-09-04 16:29'
labels: []
dependencies: []
references:
  - JF-453 (the sweeper)
  - JF-485 (the measurement protocol and the 13 registered copies)
  - ShuffleIntentHandlerTests.cs
  - 'GaplessPlaybackTests.cs:541,599'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-485 leak measurement (2026-09-04): after registering the 13 private inline GUID-temp-dir copies, the per-run leak from those families is zero (dashed-GUID and jf300 counts byte-flat across runs), but a measured residual +9 dirs per full-suite run remains from three files NOT in JF-485's list:

- ShuffleIntentHandlerTests.cs: the ctor mints shuffle-test-<guid> and Dispose deletes it, but +7/run survive, each containing a queue_<deviceId>.json written during the run.
- GaplessPlaybackTests.cs lines ~541 and ~599: gapless-dq- and gapless-rs- dirs, deleted in finally blocks, +1 each per run, same queue-json content.

Suspected mechanism (unverified): a DELAYED queue-state flush (the DeviceQueueManager persistence timer) recreates the directory AFTER the test's own deletion, so the dir survives with the late-written json. The per-run numbers are measured; the mechanism is suspected.

Fix direction: either register these dirs with PluginTempDirCleanup as well (the sweeper runs at process exit, after any flush timer has fired or the process is dying anyway: the simplest complete fix), or dispose/drain the queue manager before the test's deletion. The Register line is the low-risk shape; the drain is the root-cause shape. Also worth considering the /simplify note from JF-485: a CreateRegisteredTempDir(suffix) helper so future copies cannot forget Register.

Acceptance criteria: the shuffle-test/gapless-dq/gapless-rs families byte-flat across two consecutive full-suite runs (same measurement protocol as JF-453/485).
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
Closed complete (commit 5d409ce2).

Mechanism PROVEN (the task filed it as suspected): DeviceQueueManager arms a 2s debounce timer on every queue mutation; PersistToDisk recreates the dir and writes the json when it fires; the three leaking sites never disposed their manager, so the timer outlived the test deletion. Control groups that dispose first (DeviceQueueManagerTests, DispatchHarness) measured flat. Nuance discovered: a filtered run of the leaking class leaks ZERO (the host exits before the debounce fires); the leak needs full-suite host lifetime, which is why only full runs showed it.

Fix at both depths: root cause (the 9 test-owned managers became using-declarations, disposal strictly before the dir deletion on normal and exception paths) and belt made structural (TestHelpers.CreateRegisteredTempDir: create+register in one call; the shared EnsurePluginInstance mint and all 13 JF-485 sites migrated to it, 39 lines to 13, position-preserving). Leak table byte-flat across consecutive full-suite runs: shuffle-test 364 flat, gapless-dq/rs 66 flat, dq-test control 0 flat. The historical residue (the counts above) is the pre-fix accumulation; the JF-485 optional one-shot cleanup note covers it.

Suite 3175/3175 three times, Release 0 warnings. Review: zero findings >= 80, with the disposal ordering verified against the DeviceQueueManager.Dispose source (_disposed set before the lock; final synchronous PersistAll; timers disposed). Two sub-threshold notes recorded: the fired-and-queued PersistDevice callback nuance (unreachable in tests, no production leak path, belt would sweep) and the N-to-D GUID format shift at 4 migrated sites (attribution-preserving). The ~16 remaining raw mints in unchanged files are uniformity-only churn, correctly left.
<!-- SECTION:FINAL_SUMMARY:END -->
