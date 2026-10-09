---
id: JF-842
title: >-
  VideoAudioControllerTests StreamVideoAudio_FfmpegFailsFastWithoutOutput_Returns500
  is flaky under full-suite load on net10.0
status: Done
labels:
  - tests
priority: low
---

## Description

Observed 2026-10-09 on the JF-814 worktree (agent-af77920eafddbeb3a): a full
`dotnet test` run failed exactly this test on net10.0
(`Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs:638`),
while net9.0 was green in the same run and the two prior full runs were green on
BOTH TFMs. The test passes in isolation and on the immediate full-suite rerun
(5529/5529 net10.0). No source in its area was touched by JF-814 (handler/locale/
template change only), so this is a load/timing flake in the ffmpeg-fails-fast
test, not a regression from this task.

Fix shape: make the test hermetic and timing-tolerant (fake-script path injection
per the `WriteRecordingFakeFfmpeg` pattern; assert the 500 outcome without
depending on process-exit timing under parallel-suite CPU load).

## Mechanism (diagnosed 2026-10-09, JF-842 worker)

Line 638 is the log PIN that discriminates which of the two JF-518 500-arms ran
("exit code 3" vs "still running after ~1s"); both arms return the asserted 500,
so a red at 638 means the STILL-RUNNING arm fired (JF-842 reproduction below
confirmed this exact shape). The arm is decided by whether the spawned process
is dead when the controller's startup-wait window closes (File.Exists +
HasExited polled every 10ms, 100 times, with one final HasExited re-check; all
delays only STRETCH the window, so the child has >= ~1s). The fake is a
3-builtin `/bin/sh` script (`exit 3`): dead within microseconds of its first
CPU slice, and `HasExited` (waitpid on kernel zombie state) observes a dead
child instantly. Therefore the red requires the freshly spawned shell to get NO
first slice for the entire window. Ruled out: CPU queues alone (16 burners on 8
cores, 12 isolated runs: all green, ~300ms test duration), parent-side stalls (a
stalled observer finds the long-dead child at its next poll and takes the
exit-code arm; it cannot produce this red), the file-appeared branch (the fake
writes nothing; the cache key is a fresh GUID per test), capture-logger loss
(synchronous, lock-consistent append), and a non-3 exit code (`exit 3` is a
builtin; exec failure would be an exception at a different assert). The
remaining, environmentally consistent cause: page-in/scheduling starvation of
the first slice under memory thrash. The box sits swap-exhausted during
tonight's concurrent full-suite runs (measured 17:42 today: swap 15G/15G used,
1G RAM free, concurrent agent suites). Deliberate thrash reproduction was
denied by the permission layer (resource exhaustion on a shared box) and not
pursued; the fix does not depend on which environmental knob delayed the child.
NOT a production race: the ~1s budget before declaring "still running" is the
deliberate JF-518 product decision, and the misclassification still returns the
correct 500 and kills the process. Test-side timing assumption only.

## Fix

In the test only (`VideoAudioControllerTests.cs`): a warm-up spawn of the same
fake before the observed attempts (faults the interpreter image in unobserved,
bounded 5s), then a bounded attempt loop (8): a fresh item + controller per
attempt, the 500 contract asserted on EVERY attempt, retry until the pinned
exit-code warning appears in the capture log, and a final `Assert.True` whose
failure message dumps the captured warnings. The test now reds only when the
exit-code arm is UNREACHABLE (a code regression), not when the machine loses
one scheduling race. No production change.

## Verification

Red/green probes (all on net10.0, single test):
- flake shape forced (fake sleeps 2s on attempt 1 only): GREEN in 2s (the
  retry recovers the pin on attempt 2; the same shape reds the pre-fix test
  by construction, since one attempt was all it had);
- unrecoverable shape (fake sleeps 2s on EVERY invocation): RED at the pin
  after 8 attempts, 500 held on all 8, failure message dumps 8x
  "still running after ~1s" (the exact filed flake, reproduced deterministically);
- committed fast fake: GREEN.

(5x class runs both TFMs + the one full suite both TFMs: final tree, 2026-10-09:
class 187/187 on ALL 10 runs (5 rounds x net9.0 + net10.0); full suite 5585/5585
on net9.0 AND net10.0; final build 0 warnings 0 errors. All runs with
NUGET_PACKAGES=/var/tmp/nuget-pkgs NUGET_HTTP_CACHE_PATH=/var/tmp/nuget-http.)

## Gates

/simplify (4 agents: reuse / simplification / efficiency / altitude):
- APPLIED (reuse F1): both capture-log enumerations go through
  `TestCaptureLogger.Snapshot` (the documented lock-consistent contract; the
  dominant idiom in this file) instead of the live list.
- APPLIED (simplification F1): the warm-up `using` switched to the declaration
  form matching the file's Process.Start siblings.
- APPLIED (simplification F2): the mutable `exitCodeArmObserved` flag replaced
  by a local predicate (the pin condition defined once, no derivable state).
- Clean: efficiency (nothing wasted; happy path is warm-up + one attempt);
  altitude (retry is the only test-side mechanism for OS-scheduling
  nondeterminism once production is judged correct; one call site today, so no
  shared helper per the extraction-on-convergence discipline; if a second
  arm-pinning test appears, the convergence home is this test class, not a
  project-wide helper).

/code-review high (3 findings):
- APPLIED (F1): the warm-up force-kills on WaitForExit timeout. On the exact
  thrash class JF-842 documents, a still-runnable warm-up shell at test end
  would otherwise be named by the JF-731 Dispose backstop's temp-dir /proc
  sweep and Assert.Fail the test as a phantom leaked encode.
- ACCEPTED with rationale (F2, surfaced for the record): the retry weakens the
  pin from "fires on one deterministic run" to "fires at least once in 8
  attempts", so a CODE regression that makes the exit-code arm fire ~1-in-8
  would present exactly like the tolerated environmental flake. Unavoidable
  cost of any tolerance: unit tests cannot separate code nondeterminism from
  scheduler noise, and the alternative is the flake itself. The residual is
  documented in the test's doc comment ("reds only when the arm is
  UNREACHABLE").
- SKIPPED (F3, consistency only): the sibling still-running pin
  (StreamVideoAudio_FfmpegStillRunningAtWindowExpiry_Returns500) still reads
  the live logRecords list while this test now uses Snapshot. Outside the
  JF-842 diff (no drive-by refactor); safe in practice because that warning is
  logged synchronously inside the awaited call. A future touch of that test
  should switch it to Snapshot.
- Cleared by the review (for confidence): no "exit code 3" false-match on
  signal exit codes; CreateController overload resolution and ffmpegPath
  assignment equivalence; the static encode gate cannot starve the sequential
  retry (each attempt's slot releases within one 500ms exit-poll tick); fresh
  GUIDs keep cache keys, pins, and Moq setups disjoint; the CA3003 pragma
  matches the file idiom.

