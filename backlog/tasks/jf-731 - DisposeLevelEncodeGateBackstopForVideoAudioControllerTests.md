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

## DoD Evidence (2026-10-04)

Test-infrastructure-only change (one test class + two read-only internal test
seams); boxes #4-#8 are the template's production-surface items and are N/A
here, checked with that note: no session attributes, no HttpClient, no
interaction-model/NLU/E2E/locale surface touched.

- #1/#3: full-solution Debug build 0 warnings 0 errors; the changed test
  project also passes `dotnet build --configuration Release --no-restore
  -warnaserror` clean (the JF-736 xUnit-analyzer class of failure checked),
  re-verified on the final post-review state.
- #2: full suite ONCE on the final state (after BOTH gate rounds):
  `dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1` = 5078/5078 passed
  net9.0 (1m51s) AND 5078/5078 passed net10.0 (1m51s), the stated baseline
  exactly. Filtered class runs (266 tests) green at ~42s per TFM,
  wall-clock neutral versus the JF-730-closed class (~42s).
- #9: /simplify ran as 4 parallel angle agents (reuse/simplification/
  efficiency/altitude). Applied: the registry-keys + gate-capacity reads
  moved from stringly reflection to typed InternalsVisibleTo seams (3 angles
  converged); the pid sweep root reuses `_cache.CacheDir`; the return tuple
  lost its derivable GateRefilledToConfiguredCap element (the
  `|| !gateRefilled` clause was dead: every not-refilled path already had
  killedLeftover=true); the zombie-corner and xUnit-aggregation rationales
  deduped to one statement at the decision point; the shared message prefix
  hoisted. Skipped with reasons: GateField's own uncached reflection
  (pre-existing helper, outside the diff, leak-path-only frequency);
  DescribeKeys' Take(5) cap kept (one conditional, message robustness).
- #10: /code-review high ran on the final diff (forked review, 5 findings,
  all in-diff, NONE out-of-scope, so the reserved JF-741 number was not
  needed). Applied: R1 the pid-file read (fence probe AND the kill helper)
  gained the debris-cleanup IO guard (Exists-then-Read races a monitor's
  directory delete; an unguarded throw escapes the finally and REPLACES a
  body failure - the real masking path); R2 the assert's attribution wording
  made honest (a post-sweep-arrival zombie lands on the successor, now said
  in both the message and doc constraint 2); R3 the drain's kill count
  switched to DISTINCT targets (per-pass re-kills of one slow-to-die zombie
  no longer read as dozens); R4 the two copy-pasted death fences single-homed
  into FenceSpeedEncodesDeadAsync with the pid-parse guard extracted as
  TryReadLivePidFromVariantDir; R5 applied in CORRECTED form - the reviewer's
  "Keys already returns a list, drop the ToList" claim fails at the C# type
  level (ConcurrentDictionary.Keys is typed ICollection, CS0266 on the
  IReadOnlyList return), so the ToList stays and the doc states the snapshot
  property truthfully.

## Final Summary

Landed 2026-10-04. The class-level backstop lives in
VideoAudioControllerTests.Dispose (JF-731): every test's teardown now (a)
kills every live speed encode through the registry (via the new
`LiveSpeedEncodeCacheKeysForTest()` snapshot seam, JF-647 family, sibling of
the existing per-key seam), (b) sweeps the whole per-test cache tree
(`_cache.CacheDir`) for fake-ffmpeg pid files covering the
faulted-before-registration corner, and (c) only when a kill proves a leak,
drains the gate to its CONFIGURED cap (the new
`EncodeGateConfiguredCapacityForTest` seam, read fresh per poll - constraint 1)
in the JF-730 re-arming kill-and-poll shape, then Assert.Fails IN Dispose so
the red lands on the leaking test itself.

Constraint 2 (assert must not mask) was settled EMPIRICALLY, not by the
filing's suggested workarounds: a scratch probe plus the in-situ sabotage run
showed xUnit 2.7.0's DisposeTestClass AGGREGATES a body exception and a
Dispose exception (one AggregateException, both messages, both stack traces,
inner #1 = body, inner #2 = Dispose) on both TFMs - the filing's "a Dispose
throw replaces the in-flight exception" premise does not hold on this xUnit,
so the simplest honest design (assert-in-Dispose, AFTER the cleanup runs) is
safe and gives per-test attribution with no next-test indirection.

Constraint 3 (registry corner) is stated once, at the decision point: the
registry half covers only registered encodes, the pid half only pid-writing
fakes, a post-sweep arrival escapes both (JF-704 machinery owns that), and
the one deliberate hole - an unregistered, pid-less LIVE slot holder - is the
zero-kill early return's measured trade: instrumented runs showed EVERY
zero-kill drain wait (532 Disposes, all kills=0, max 601ms) was the legit
mid-release transient of an already-exited encode, ~19.6s of pure waiting
per TFM if waited out, so the drain is kill-gated. One such zombie costs one
slot; a full-gate stall needs two of a shape no current fake constructs.

The per-test finallys' disposition (decided explicitly): the JF-730
SparesOtherDevices teardown is REDUCED to its kill half (its drain loop and
gateRefilled assert deleted; the class backstop owns both once), and the
JF-668 finally is KEPT as the kill half it already was - both as
defense-in-depth documenting each scenario's own encodes. Both kill halves
gained a fence from the code-review round: a bounded wait-for-observed-death
(`SpeedEncodeStillAlive`, with the disposed-Process-is-dead guard the
production watcher uses) so a correctly self-cleaned run cannot false-red in
Dispose on kill-delivery timing (Kill returns before the exit is
observable); the fence's first run caught that guard's absence live.

Sabotage red proofs (temporary leaked sleep-300 speed encode, run then
removed; re-proven after EACH gate round, final run on the true final shape,
both TFMs): the leaking test reds via its own Dispose assert in ~0.5s ("1
registry kill target(s) (keys: <guid>-speed-1500), 0 pid-file kill
target(s) ... gate refilled to its configured cap"), the other 266 tests
stay green, and the class runs ~42s per TFM including the leaker - the next
test unblocked, the 299.42s wall-clock failure mode gone. The masking
variant (body assert fails AND the encode leaks) reports BOTH failures in
one AggregateException with separate stack traces.

The code-review round also hardened the two kill-half finallys the backstop
made load-bearing: both now fence their kills to OBSERVED death
(FenceSpeedEncodesDeadAsync, 5s bounded) because Kill returns before the
exit is observable and the backstop counts a still-alive process as a leak -
the fence's first run caught its own missing disposed-Process guard live
(a monitor-disposed registry entry makes HasExited THROW, not return false;
now treated as dead, the production exit watcher's convention).
