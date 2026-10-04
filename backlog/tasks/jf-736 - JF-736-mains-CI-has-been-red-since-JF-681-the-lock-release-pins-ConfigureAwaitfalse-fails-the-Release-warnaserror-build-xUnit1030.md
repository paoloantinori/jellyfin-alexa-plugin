---
id: JF-736
title: >-
  JF-736 - main's CI has been red since JF-681: the lock-release pin's
  ConfigureAwait(false) fails the Release -warnaserror build (xUnit1030)
status: To Do
assignee: []
created_date: '2026-10-03'
updated_date: '2026-10-04 08:17'
labels:
  - ci
  - test-infrastructure
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 by the JF-726 worker (reserved number), discovered while
verifying the "no new compiler warnings" DoD item for the JF-726 pin: the
xUnit1030 warning the local build surfaced is NOT new with JF-726. It is a
pre-existing site that has been failing main's CI build for two days.

EVIDENCE (all read 2026-10-03, from the JF-726 worktree on 4f0f4ee5):

- The failing site: `StreamHlsVideoAudio_ThrowingInLockProbe_ReleasesTheItemLock`
  (Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs,
  line 1518 on main), landed in commit 4386d070 (JF-681, 2026-10-02 02:36 UTC):
  `await _cache.LockItemAsync(...).WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);`
  inside an `async Task` TEST method. xunit.analyzers 2.7.0 flags that shape as
  xUnit1030 ("Test methods should not call ConfigureAwait(false), as it may
  bypass parallelization limits").
- Local: `dotnet build Jellyfin.Plugin.AlexaSkill.Tests` emits the warning in
  BOTH Debug and Release (one per TFM).
- CI: `ci.yml` build-and-test runs `dotnet build --configuration Release
  --no-restore -warnaserror` over the solution (the test project IS in the
  sln), so the warning is an error there. Run 37144567905 (main @ 4f0f4ee5)
  fails exactly there: `VideoAudioControllerTests.cs(1518,50): error xUnit1030
  ... 2 Error(s)` (one per TFM).
- Streak: the LAST green CI run is 7bc89eb1 (2026-10-02T00:24Z); 4386d070 is
  an ancestor of the first failing run's commit d05958e3 (2026-10-02T02:53Z);
  every run since (25+ runs, ~2 days) is a failure with this same error. So
  pushes to main currently have NO green build signal, and any new warning
  anyone introduces hides inside an already-red job.

WHY IT WAS NOT CAUGHT IN JF-681: its worker's suites and gate runs were
`dotnet test` (Debug, no warnaserror), where xUnit1030 is a warning that
scrolls by; the JF-681 task evidence even records "0 warnings", which was read
from the test run summary, not a `-warnaserror` build.

FIX OPTIONS (pick one consciously):

1. Drop `ConfigureAwait(false)` at that one site (the await then continues on
   the xUnit synchronization context; the acquisition is already bounded by
   its own 15s WaitAsync, and the test is not parallelization-sensitive: it is
   the ONLY ConfigureAwait left inside a test method body in the file; helper
   methods outside test methods, like ObserveStrandedEndpointAsync, are not
   flagged and can keep it).
2. `ConfigureAwait(true)`: silences xUnit1030 while keeping the shape
   explicit; same runtime effect as option 1.
3. Project-wide `NoWarn` for xUnit1030 in the test csproj, which would hide
   future real instances of the same class in one broad stroke; recommended
   AGAINST, since there is exactly one offending site today.

RECOMMENDATION: option 1 (smallest change that restores CI), plus a one-line
consideration of adding `dotnet build -warnaserror` (any config) to the local
pre-commit gate recipe so a warning class like this reds in the worker's own
run instead of two days later on CI.

FINDING 2 (filed same-turn by the JF-726 /simplify round; the reuse,
simplification, altitude, and efficiency angles ALL converged on it, and it
was out of that worker's sanctioned single-file surface): the JF-726 pin
introduced a private `InstructionOperands` byte-walk in
Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs that
is a near-verbatim copy of the shared scanner's private
`IlCallScanner.OperandTokens`
(Jellyfin.Plugin.AlexaSkill.Tests/Handler/IlCallScanner.cs, same null-body
guard, Array.Empty fallback, `i + 5 <= il.Length` window, opcodes.Contains)
plus the one thing the scanner does not expose: the instruction OFFSET (the
JF-726 birth-ordering assert needs backstop-call-before-first-Assert as an
ordering fact). The same local family adds `LdcI4Operands` (int32 operands of
ldc.i4 instructions), a capability the scanner has never had. COST of leaving
the walk file-local: the operand-window discipline ("a coincidental byte match
can only ADD a candidate, loud-only") now lives in two places, and a future
fix to it in the scanner silently misses the copy; the next roster needing
offsets or constant scans copies from the test file instead of the scanner.
The scanner's own documented history is folding exactly these copies (JF-582
created it from 4+2 private walk copies, JF-631 the resolver, JF-634 the walk
scaffolding, JF-699 CallsNamedMethod/SameTypeHelpers). FIX when
IlCallScanner.cs is next in an edit surface: graduate an
`InstructionOperands(method, opcodes)` yielding (offset, operand) into the
scanner (expressing CallTokens/NewobjTokens/OperandTokens over it), add the
ldc-int-operand surface beside them, and make the JF-726 pin delegate like
every other roster consumer; then delete the private copy.

GATE-MARKER ANNOTATION (2026-10-03, JF-726 orchestrator review): FINDING 1 (the CI-red
discovery) is FIXED on main by 9353de90, verified green (CI run 37149291498; the fix
removed the ConfigureAwait(false) from the [Fact] test method - xUnit1030 fires only on
test methods, and ConfigureAwait is a no-op in xUnit anyway). This task's LIVE scope is
FINDING 2 ONLY (the IlCallScanner.OperandTokens graduation: the offset-walk plus ldc
surface moves into the shared scanner, CallTokens/NewobjTokens/OperandTokens expressed
over it, the JF-726 copy deleted; the phantom-window opcode-aware-walk fix rides it).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 The xUnit1030 site resolved (ConfigureAwait dropped or made true) and `dotnet build -c Release -warnaserror` over the solution exits 0
- [x] #2 CI green again on the fixing push (build-and-test job)
- [x] #3 The InstructionOperands offset walk + ldc-int surface graduated into IlCallScanner (or consciously declined with a reason on this task)
- [x] #4 dotnet test passes both TFMs
- [x] #5 /simplify + /code-review high passed
<!-- DOD:END -->

## DoD Evidence (Finding 2, the live scope)

- #3 DONE 2026-10-04 (JF-736 worker, worktree branch). The graduated surface
  is `IlCallScanner.InstructionOperands(method, params byte[] opcodes)`
  yielding (Offset, Operand) pairs; `OperandTokens` (and with it
  `CallTokens`/`NewobjTokens`) is now a Select projection over it, so the
  operand-window discipline lives in ONE definition. `LdcI4Operands` sits
  beside them. The JF-726 pin's private trio
  (CallInstructionOffsets/LdcI4Operands/InstructionOperands) is DELETED from
  VideoAudioControllerTests.cs and the pin delegates at all three use sites
  (the two call-offset walks, the ldc assert), plus the stfld
  argument-identity tie's every-offset loop, which the /simplify altitude
  round caught as a fourth surviving copy of the same window shape (now
  `InstructionOperands(birthMethod, 0x7D)`).
  PHANTOM-WINDOW VERDICT: the phantom case is REAL and the pin's walk did NOT
  handle it. The pin's own comment (pre-fix) recorded the residual: a 0x20
  byte inside another instruction's operand bytes could satisfy the ldc
  exact-value Contains in the GREEN direction, against the loud-only
  discipline. The fix rides the graduation exactly as the filing prescribed:
  the graduated walk is OPCODE-AWARE (it decodes instruction boundaries, with
  opcode lengths and operand sizes derived from the runtime's own
  System.Reflection.Emit.OpCodes table, the switch opcode's variable operand
  included), so only a real instruction of the requested opcode yields.
  RED-PROOFS (both documented by run): (a) the JF-726 pin passes on the real
  IL through the scanner (266/266 VideoAudioControllerTests, then the full
  suites); (b) sabotaging the graduated walk's ldc filter
  (`&& opcode != 0x20`) reds the pin with its own JF-704/JF-726 message
  (Verified: 1 failed / 0 passed on `FullyQualifiedName~BirthBackstop`,
  reverted).
- #4 DONE 2026-10-04: full suites on the final state,
  `dotnet test Jellyfin.Plugin.AlexaSkill.Tests` per TFM: net9.0 5111/5111
  PASSED, net10.0 5111/5111 PASSED (baseline 5109 + the two new decoder
  tests; no test removed). Release CI-discipline build
  `dotnet build Jellyfin.Plugin.AlexaSkill.sln -c Release --no-restore
  -warnaserror`: 0 warnings, 0 errors.
- #5 DONE 2026-10-04: /simplify ran as 4 parallel angle agents on the diff.
  Applied: the stfld fourth-copy graduation (altitude), deletion of the
  derivable `InstructionOperandsRequestedOpcodes` guard dictionary and its
  up-front validation (reuse + simplification F1/F2/F3: the invariant was
  encoded three times; the loud-only discipline makes the guard redundant),
  and the unreachable truncation fallback in `SwitchOperandBytes`
  (simplification F4). Skipped with reason: the four efficiency findings
  (flat arrays instead of the short-keyed dictionary, a hoisted first-opcode
  compare, non-params overloads, one fewer iterator layer), every one judged
  NOT worth it at test-infra scale by the reviewer (tens of ms per
  minutes-long suite) and the diff is net-positive (the Contains check moved
  from per-byte to per-instruction).
  /code-review high (background skill, full diff) returned 5 findings, ALL
  applied: F1 the decode's silent `break` on an unknown opcode violated the
  loud-only contract (now THROWS; the decode was restructured so the ONE
  boundary decoder is `IlCallScanner.Instructions(byte[])`, consumed by both
  the operand walk and a direct test); F2 banned "word - word" prose in an
  authored doc comment (reworded to parentheses); F3 the truncated-switch
  read was the only unguarded truncation path (now every malformed-input path
  throws, consistent and loud); F4 the switch-is-minus-one fact was encoded
  twice (OperandByteSizeOf now owns the single encoding); F5 the decoder had
  no direct test (new IlCallScannerTests: one test asserts the decoded chain
  consumes EVERY real body of the plugin+test assemblies exactly from offset
  0 to the IL end, the other pins the synthetic branches real IL never
  contains: the 16-bit local form, ldftn, switch, and the malformed-input
  throws; the decoder-table sabotage was red-proven: an InlineVar size flip
  reds the synthetic test). Because those fixes restructured the decoder, a
  SECOND /code-review high pass ran on the final state and returned 5 more
  findings, ALL applied: R1 an operand overrunning the stream end exited the
  loop silently (the loudness contract's one remaining hole; now throws, with
  the switch count measured in long so a malformed huge count cannot overflow
  into a backwards walk), R2 the graduation's doc had overstated the JF-726
  ldc pin as the only GREEN-direction phantom consumer (the scanner's own
  Contains-family is equally exposed; wording corrected in code and here),
  R3 the truncated-switch-count assert pinned ThrowsAny instead of the exact
  ArgumentException, R4 a vacuous can-never-fail length assert in the
  exhaustive body test (removed), R5 the LdcI4Operands hoist had dropped the
  short-encoding totality argument the deleted private helper's doc carried
  (restored: non-negative constants carrying the JF-726 bits exceed the short
  encodings). No real out-of-scope finding emerged in either round, so the
  reserved JF-747 number was NOT consumed.

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed on its worktree branch by the JF-736 worker (live scope: Finding 2;
Finding 1 was already fixed on main by 9353de90). The graduation landed in the
direction the filing prescribed and one step deeper, because the code-review
round found the first cut had re-introduced a silent-loss mode into a scanner
whose whole contract is loudness.

THE GRADUATED SURFACE: `IlCallScanner.InstructionOperands(method, opcodes)`
yields (offset, operand) pairs for single-byte opcodes with a 4-byte int32
operand (call/callvirt/newobj/ldc.i4/stfld/ldfld/ldstr...); `OperandTokens`
and therefore `CallTokens`/`NewobjTokens`/`ContainsCallTo*` are Select
projections over it; `LdcI4Operands` is the new int32-constant surface. The
operand-window discipline lives in exactly one definition for the first time
since JF-582.

THE PHANTOM-WINDOW FIX: the walk is now OPCODE-AWARE. It decodes instruction
boundaries instead of testing the opcode byte at every offset, with opcode
lengths and operand sizes derived at type-init from the runtime's own
System.Reflection.Emit.OpCodes table (no hand-maintained ECMA table to drift;
the switch opcode's variable operand measured per instance). This closes the
phantom case for real: previously a coincidental byte sequence inside another
instruction's operand could add a candidate. The roster EQUALITY consumers
were safe by direction (a phantom add failed the equality loudly); every
BOOL/Contains consumer could pass GREEN on one, and not only the JF-726 ldc
pin's exact-value Contains: the scanner's own ContainsCallToToken /
CallsGetter / ConstructsType family equally (the first draft of this summary
named the ldc pin as the sole exposed consumer; the final review corrected
that inventory). Now only a real instruction of the requested opcode yields.
The boundary decode itself is exposed as `Instructions(byte[])` and directly
pinned by the new IlCallScannerTests: every real method body of the plugin
and test assemblies must decode from offset 0 to the exact IL end with no
gaps (a wrong operand size desyncs some body and names it), and the branches
real IL never contains (16-bit local form, ldftn, switch, the malformed-input
throws) are pinned synthetically. Malformed input now throws everywhere
instead of silently truncating (code-review F1: the first cut's `break` on an
unknown opcode was the scanner's first silent-loss mode; the final pass
closed the last hole, an operand overrunning the stream end).

PER-CONSUMER DISPOSITIONS: every roster consumer (CallTokens/NewobjTokens/
ContainsCallTo*/ConstructsType/CallsGetter/CallsNamedMethod/SameTypeHelpers/
CallsDirectlyOrViaSameTypeHelper) is unchanged at the call site; they inherit
the opcode-aware window through the shared walk, verified by their suites
(51/51 filtered, then full 5111/5111 per TFM). The JF-726 pin
(ServeInLockWarmCacheHelper_BirthBackstop) delegates at all its walk sites and
its private trio is deleted; its inline emission-tie byte reads (the ldfld
byte check, the nop-prefix ldarg.0 check) stay, deliberately: single-byte
shape inspection is not operand-walk duplication (the altitude round judged
and cleared them). The stfld argument-identity tie's loop, which survived the
first cut as a fourth every-offset copy, was graduated too.

RED-PROOFS: the pin green on real IL through the scanner; the ldc-filter
sabotage reds the pin with its own JF-704/JF-726 message; an InlineVar
operand-size sabotage reds the synthetic decoder test (the first attempt at
that sabotage exposed an honest coverage gap, the 16-bit family never occurs
in real bodies, which is exactly why the synthetic test exists).

GATES: /simplify (4 angles) applied 3 findings, skipped 4 efficiency findings
with the reviewer's own not-worth-it-at-scale verdicts; /code-review high ran
TWICE (the decoder restructure landed between the rounds): round 1 returned 5
findings, all applied (F1 loud-only throw, F2 prose rule, F3 consistent
malformed-input handling, F4 single encoding of the switch fact, F5 the
direct decoder tests); the final-state round returned 5 more, all applied
(the silent operand-overrun exit, the phantom-consumer inventory correction,
the exact exception-type pin, the vacuous assert removed, the restored
short-encoding totality argument). JF-747 was not needed: no real
out-of-scope finding emerged in either round. Suites net9.0 5111/5111,
net10.0 5111/5111; Release -warnaserror clean.
<!-- SECTION:FINAL_SUMMARY:END -->
