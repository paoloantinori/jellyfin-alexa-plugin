---
id: JF-421
title: >-
  Encode gate capacity check compares free slots from the per-request ctor:
  JF-310 concurrency bound never binds
status: To Do
assignee: []
created_date: '2026-08-31 15:02'
updated_date: '2026-09-01 06:07'
labels:
  - code-review
  - resource-limit
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs:1551'
  - 'Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs:117'
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review finding (2026-08-31, high effort, CONFIRMED by code reading). VideoAudioController.cs:1551 (UpdateEncodeGateCapacity), called from the per-request constructor path (line 117).

DEFECT: UpdateEncodeGateCapacity compares the semaphore's FREE slots (CurrentCount) to the configured capacity and is invoked per request, so the gate is swapped whenever ANY encode is in flight. Walkthrough with cap 2: E1 holds a slot on G0; the ctor of the next request (including cheap segment fetches) sees CurrentCount=1 != 2 and installs a fresh SemaphoreSlim(2,2); E2 acquires the new G1; the next request swaps to G2; E3 acquires G2. Unbounded concurrent ffmpeg processes under exactly the concurrent-encode load the JF-310 gate exists to bound. The same wrong check silently ignores a lowered cap (2 -> 1 with one slot held: CurrentCount==1==bounded returns early, no swap).

FIX SHAPE: detect a capacity change against the semaphore's ORIGINAL/max capacity, not CurrentCount. Options: track the configured cap alongside the semaphore and compare configs; or construct SemaphoreSlim(initialCount: available, maxCount: newCap) logic deliberately. Ensure in-flight holders finish on the old instance gracefully (accept that a swap while busy waits for old holders via disposal discipline; SemaphoreSlim.WaitAsync handles multiple instances only if the code always references the current field atomically).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The gate is swapped only when the CONFIGURED capacity changes, not when in-flight encodes change the free-slot count (cap 2 + 1 encode running must keep the same semaphore instance)
- [ ] #2 Concurrent encodes are actually bounded: with cap N, at most N ffmpeg encodes run concurrently (unit test with fake encode tasks)
- [ ] #3 Lowering the cap while a slot is held takes effect for NEW acquisitions (documented/labeled behavior if full enforcement waits for release)
- [ ] #4 Regression: JF-310's original concurrency purpose still holds (no unbounded encode path)
- [ ] #5 Remove the dead holdGateUntilExit parameter and its never-taken branch in StartFfmpegProcessGatedAsync (all 3 callers pass true since the faststart remux stopped using this method; the 2026-09-01 audit confirmed the branch is unreachable) - same method this task reworks, so land together
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-01 audit of untracked review recommendations: the dead holdGateUntilExit parameter (flagged in the 2026-08-31 pass-1 and pass-3 cut lists, survived the JF-428 rewire, verified still unreachable: callers at :227/:403/:707 all pass true) is added as an AC here since it lives in the same method this task fixes.
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
