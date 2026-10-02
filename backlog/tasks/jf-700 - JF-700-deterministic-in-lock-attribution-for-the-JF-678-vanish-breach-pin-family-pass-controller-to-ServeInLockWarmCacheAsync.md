---
id: JF-700
title: >-
  JF-700 - deterministic in-lock attribution for the JF-678 vanish/breach pin
  family (pass controller to ServeInLockWarmCacheAsync)
status: To Do
assignee: []
created_date: '2026-10-02 01:42'
labels:
  - encode-gate
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-681 -
    JF-681-test-infrastructure-residuals-of-the-own-live-prewrite-pins-mid-registration-split-uncovered-attribution-seam-prewrite-read-count-park-assert-fault-observation.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the JF-681 simplify round (altitude/reuse finding, cut at the task's four-item scope cap; JF-681 landed the seam and moved the assert into the helper).

JF-681 gave the in-lock pins a deterministic in-lock-vs-fast-path attribution: the InLockWarmCacheProbeForTest seam fires at the ONE LockHlsItemAsync wrapper, and ServeInLockWarmCacheAsync (VideoAudioControllerTests) gained an optional `VideoAudioController? controller = null` parameter - when passed, the helper wires the seam and asserts the probe fired after the endpoint completes, so a >400ms pre-lock stall whose request then served from the FAST path fails the pin instead of passing under a false attribution. EIGHT pins opted in (the four JF-677 in-lock twins, the two JF-680 own-live prewrite pins, the two JF-681 mid-registration pins).

THE RESIDUAL: the JF-678 vanish/breach family of ServeInLockWarmCacheAsync callers (the InLockCacheVanishedAtServe twins and the breach pins: ~8 call sites at the former lines 7514/7553/7600/7712/7808/7869/7931/7981 on the pre-JF-681 file, find them by grep ServeInLockWarmCacheAsync minus the 8 opted-in pins plus the fault-observation pin) still attribute in-lock-vs-fast-path via the 400ms park assert ALONE - the exact probabilistic attribution JF-681 set out to close, now silently asymmetric across the same helper.

THE WORK (mechanical, test-only): pass `controller` as the 4th argument at every family call site whose endpoint enters a lock scope. CAVEATS: (a) the BREACH pins expect the endpoint task to THROW (FileNotFoundException propagation), and the helper's probe assert sits after `await endpointTask.WaitAsync(...)` inside the helper, so a throwing endpoint never reaches it - for those sites the deterministic attribution needs either a helper variant that asserts before the rethrow, or an explicit probe assert in the pin around the ThrowsAsync; decide per pin honestly, do not fake coverage. (b) one call site (the fault-observation pin) deliberately passes no controller - leave it. Red proof: removing the LockHlsItemAsync invoke flips exactly the pins that opted in (JF-681 verified this shape at 8/17).

VERIFICATION: full suite both TFMs (recipe: dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1, never --no-build); the 8 already-opted-in pins stay green; no production change.
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
