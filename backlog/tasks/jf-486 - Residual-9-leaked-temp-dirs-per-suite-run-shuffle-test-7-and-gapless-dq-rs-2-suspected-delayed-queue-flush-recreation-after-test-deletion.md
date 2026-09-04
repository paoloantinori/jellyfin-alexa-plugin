---
id: JF-486
title: >-
  Residual +9 leaked temp dirs per suite run: shuffle-test (7) and gapless-dq/rs
  (2), suspected delayed queue-flush recreation after test deletion
status: To Do
assignee: []
created_date: '2026-09-04 15:58'
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
