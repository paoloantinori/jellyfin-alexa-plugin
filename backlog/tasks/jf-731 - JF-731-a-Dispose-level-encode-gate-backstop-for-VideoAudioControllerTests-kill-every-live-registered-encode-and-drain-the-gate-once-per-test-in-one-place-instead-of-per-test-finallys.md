---
id: JF-731
title: >-
  JF-731 - a Dispose-level encode-gate backstop for VideoAudioControllerTests:
  kill every live registered encode and drain the gate once per test, in one
  place, instead of per-test finallys
status: Done
assignee: []
created_date: '2026-10-03'
updated_date: '2026-10-04 01:22'
labels:
  - test-infrastructure
  - reliability
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 from the JF-730 gate-marker round (finding F5, reserved number
carried over unused by JF-730 itself). JF-730 removed a 299.42s-per-TFM class
stall caused by ONE test leaving two live fake-ffmpeg encodes alive, each
holding an encode-gate slot (default cap 2; the gate releases a slot only on
the process's own exit, observed by a 500ms exit-poll). The fix was per-test:
that test's finally now kills its encodes through the
`LiveSpeedEncodeProcessForTest` registry (hoisted as the shared
`KillLiveEncode(cacheKey)` helper, also called by the JF-668 pin's finally),
backs the registry up with the fake's pid files, and asserts the gate refilled
to its configured cap before returning.

THE STRUCTURAL IMPROVEMENT this task tracks: the same immunity can be owned
ONCE at the class level. VideoAudioControllerTests already implements
IDisposable (the per-test Dispose currently only deletes _tempDir); extending
that Dispose to (a) kill every live entry the speed-encode registry still
holds and (b) wait for the encode gate to drain (bounded) would make the whole
class immune to ANY future test leaking a gated encode, in one place, and the
two per-test finallys would become redundancy that can then be retired or kept
as documentation.

DESIGN CONSTRAINTS from JF-730's measured experience, load-bearing for whoever
picks this up:

1. The refill/drain target must be the CONFIGURED cap (2), never a count
   captured at test entry: an entry snapshot captures whatever transient drain
   a PRIOR test's exit-poll was mid-flight (a 500ms window), and the teardown's
   own drain then fails the compare. This exact false red was observed live on
   both TFMs during JF-730 and is why the per-test assert targets the constant.
2. A Dispose-level ASSERT (not just a best-effort drain) must not fire when the
   try body already failed: a finally/Dispose throw replaces the in-flight
   exception and destroys the original failure's diagnostics. JF-730's shape
   (drain inside the finally, assert after the try/finally, on the success path
   only) is the pattern to generalize; a Dispose cannot distinguish those
   paths, so the assert-in-Dispose question needs its own answer (for example:
   drain in Dispose, assert in a single class-level guard test that runs last,
   or assert only when xUnit reports the test body completed).
3. The registry covers only REGISTERED encodes; an encode faulting between
   process start and registration escapes it (JF-730's pid-file backstop
   covers this only for fakes that write ffmpeg.pid first). A class-level
   sweep has the same corner and should state it honestly in its doc.

Acceptance: the class-level backstop lands with the constraints above
addressed, the per-test finallys' status decided explicitly (retired or kept
with a reason), both TFMs' full suites green, and a sabotage red proof (a
temporary leaked sleeper in some test must fail the new backstop, not the
suite's wall clock).
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle including a rework round: worker commits 0b846bc6 (rebased) + 5d53a757 (rework + gate refresh), merged as 455a1ba7. The Dispose-level encode-gate backstop: the per-test Dispose kills every live speed encode (the registry half) plus a pid-file sweep and a NEW /proc temp-dir process half (every fake script under the test instance's own guid-suffixed temp dir, parallel testhosts disjoint, ambient processes untouched - the half's first run caught a real residual leak invisible to the original halves, and sabotage v2, an episode-path sleeper with no pid file, reds in ~0.6s), refills the gate UNCONDITIONALLY (the gate refresh's GR1 reverting the kill-gating: a stuck slot with nothing killable must red, not hang the next test; the ~18s/TFM refill price accepted and documented), and asserts in Dispose (xUnit 2.7.0 aggregates body+Dispose failures, probe-verified by both the worker and the orchestrator reviewer on the pinned stack). The JF-730 teardown reduced to kill+fence, the fence consolidated to one temp-dir-probe shape on TestHelpers.WaitUntilAsync, and GR2's dash-tail-exec fix (WriteFakeFfmpeg appends exit $?) keeps the script path in the cmdline on CI's /bin/sh where the process half's match would otherwise break - a CI-fatal difference invisible on Fedora. The two production seams read-only. Worker gates green on both rounds (simplify; code-review high 5+5 applied incl. the GR1/GR2 catches); the orchestrator gate-marker verified all five axes with its own pinned-stack probe, its 4 findings all landed via the rework (the coverage-honesty corrections, the third half landed rather than filed - JF-741 unused, the doc move, the fold, the prose, the arithmetic). Suites: worker 5078/5078 both TFMs on the exact final state (Release -warnaserror clean); orchestrator merged-tree 5078/5078 both TFMs exit 0 on both split legs at ~1m31s. Test-and-seam surface (VideoAudioController read-only seams): production behavior unchanged; no deploy needed beyond the running build.
<!-- SECTION:FINAL_SUMMARY:END -->
