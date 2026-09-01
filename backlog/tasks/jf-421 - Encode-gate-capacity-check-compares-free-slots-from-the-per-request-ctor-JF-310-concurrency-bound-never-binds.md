---
id: JF-421
title: >-
  Encode gate capacity check compares free slots from the per-request ctor:
  JF-310 concurrency bound never binds
status: Done
assignee: []
created_date: '2026-08-31 15:02'
updated_date: '2026-09-01 11:52'
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
- [x] #1 The gate is swapped only when the CONFIGURED capacity changes, not when in-flight encodes change the free-slot count (cap 2 + 1 encode running must keep the same semaphore instance)
- [x] #2 Concurrent encodes are actually bounded: with cap N, at most N ffmpeg encodes run concurrently (unit test with fake encode tasks)
- [x] #3 Lowering the cap while a slot is held takes effect for NEW acquisitions (documented/labeled behavior if full enforcement waits for release)
- [x] #4 Regression: JF-310's original concurrency purpose still holds (no unbounded encode path)
- [x] #5 Remove the dead holdGateUntilExit parameter and its never-taken branch in StartFfmpegProcessGatedAsync (all 3 callers pass true since the faststart remux stopped using this method; the 2026-09-01 audit confirmed the branch is unreachable) - same method this task reworks, so land together
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-01 audit of untracked review recommendations: the dead holdGateUntilExit parameter (flagged in the 2026-08-31 pass-1 and pass-3 cut lists, survived the JF-428 rewire, verified still unreachable: callers at :227/:403/:707 all pass true) is added as an AC here since it lives in the same method this task fixes.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-421: the JF-310 concurrent-ffmpeg bound actually binds now, and the gate swap is race-free.

WHAT CHANGED (commit e00b3b3, 2 files)
- Root cause: UpdateEncodeGateCapacity compared SemaphoreSlim.CurrentCount (FREE slots) against the configured cap, called from the per-request controller ctor. Any in-flight encode changed the free-slot count, so every subsequent request rebuilt a fresh full semaphore: cap 2 + 1 encode running admitted unbounded concurrent ffmpeg processes (CPU/disk saturation, the DoS vector JF-310 exists to bound), and each in-flight encode also pinned its cache dir (unbounded disk growth with JF-428's pins).
- Fix: _encodeGateCapacity stores the configured cap the live gate was built with; the per-request sync becomes a cheap int-compare no-op (also removing the old per-request SemaphoreSlim allocation, verified by the efficiency agent).
- Review round hardening: the check-then-rebuild runs under a lock. The unsynchronized two-field update racing a config save could leave the capacity field disagreeing with the live semaphore, and the early-return would then freeze the wrong bound for the process lifetime; the review's verifier reproduced this 295/300 rounds. Store-order swapping alone only narrows the window; the lock eliminates it. Readers stay lock-free (atomic reference capture; drain-safe semantics unchanged: in-flight holders finish on the old instance).
- Dead holdGateUntilExit parameter + never-taken branch removed (flagged twice in review cut lists, folded here per the audit rule).

VERIFICATION
- Tests split per invariant with a shared GateField() helper: unchanged-config does not rebuild under an in-flight slot (the exact old bug), a real raise rebuilds with the full count, lowering while busy binds new callers immediately. Suite 2797 passed / 0 failed; Release build 0 warnings.
- Gates: /simplify 4 agents (verified the no-op path allocates nothing, the locale rewrite surgical, per-request sync the right depth vs a config event - the plugin has no config-change hook and the PATCH endpoint does not even carry this field); /code-review high: its 2 findings on this diff both applied (the swap lock, the test split).
- Rides the next deploy; live check is the standard matrix (the gate binds during concurrent audiobook encodes, not directly observable from the simulator).
<!-- SECTION:FINAL_SUMMARY:END -->

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
- [x] #10 /code-review high passed (no blocking findings remaining
- [x] #11 or findings applied/tracked)
<!-- DOD:END -->
