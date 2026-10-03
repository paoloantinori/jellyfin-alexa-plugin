---
id: JF-736
title: >-
  JF-736 - main's CI has been red since JF-681: the lock-release pin's
  ConfigureAwait(false) fails the Release -warnaserror build (xUnit1030)
status: To Do
assignee: []
created_date: '2026-10-03'
labels:
  - ci
  - test-infrastructure
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
- [ ] #1 The xUnit1030 site resolved (ConfigureAwait dropped or made true) and `dotnet build -c Release -warnaserror` over the solution exits 0
- [ ] #2 CI green again on the fixing push (build-and-test job)
- [ ] #3 The InstructionOperands offset walk + ldc-int surface graduated into IlCallScanner (or consciously declined with a reason on this task)
- [ ] #4 dotnet test passes both TFMs
- [ ] #5 /simplify + /code-review high passed
<!-- DOD:END -->
