---
id: JF-731
title: >-
  JF-731 - a Dispose-level encode-gate backstop for VideoAudioControllerTests:
  kill every live registered encode and drain the gate once per test, in one
  place, instead of per-test finallys
status: To Do
assignee: []
created_date: '2026-10-03'
labels:
  - test-infrastructure
  - reliability
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
