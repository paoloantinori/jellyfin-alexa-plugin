---
id: JF-730
title: >-
  JF-730 - the VideoAudioControllerTests class is ~5.5 of the ~7 minutes per
  TFM; timing-attributed splitting or park-window reduction with wall-clock
  proof
status: Done
assignee: []
created_date: '2026-10-03 07:45'
updated_date: '2026-10-03 15:42'
labels:
  - test-infrastructure
  - performance
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 from the workflow-speedup analysis (Paolo's directive). The full suite
takes ~6-7 min per TFM, and the VideoAudioControllerTests class alone is ~5.5 of it
(measured across tonight's runs: the class filter runs ~5m50s per TFM against ~7m00s for
the whole suite). The class's cost is wall-clock, not CPU: hundreds of 400ms park
assertions and the settle-budget awaits (20s worst case) serialize inside ONE xUnit
collection, pinned there by the shared statics (Plugin.Instance, the encode registries,
the cache root) its tests mutate - naive parallelization into multiple collections
would race those statics.

THE WORK, in order:
1. TIMING ATTRIBUTION first: instrument or reason where the 5.5 min goes (the park
   count x 400ms; the settle-budget tests; the encode-fake I/O). Do not guess - count
   the parks (grep Task.Delay(400) and the park assert call sites) and multiply.
2. Evaluate the safe levers with before/after wall-clock proof on both TFMs:
   (a) PARK-WINDOW REDUCTION: the 400ms window exists to let the endpoint park on the
   real per-item gate before the assert; measure whether 150-250ms holds the same
   guarantee on this hardware (the parks are already timing-tolerant asserts, not
   exact sleeps); every halving of the park window halves the class's dominant term.
   (b) SETTLE-BUDGET SEAMS: the 20s EndpointSettleBudget is a worst-case bound most
   tests never reach; a test-seam override (smaller budget in tests that do not
   specifically pin the budget behavior) removes the tail.
   (c) SPLIT-BY-STATIC-OWNERSHIP: if (a)+(b) are insufficient, partition the class
   into collections grouped by WHICH statics they touch (encode-gate family vs cache
   family vs position-tracker family) so within-group serialization is preserved but
   the groups run in parallel; requires an audit of cross-family static touching.
3. Pin nothing new unless a lever changes semantics (it must not); the acceptance is
   the wall-clock delta stated in the task file and the full suite green both TFMs.

CONSTRAINTS: do not weaken any timing assertion's guarantee (the parks prove real
concurrency against real ffmpeg); the JF-677/JF-680/JF-681/JF-700/JF-704 pin families
stay green UNCHANGED; CI equivalence (the class's behavior must not become
hardware-flaky - if a reduced window is adopted, state the measured margin).

### ATTRIBUTION RESULT (measured, 2026-10-03, TRX per-test durations, both TFMs)

The task's premise is REFUTED by the artifact. The parks and the settle budgets are
second-order; the class's cost is ONE TEST BLOCKED BEHIND A LEAKED ENCODE GATE:

- `StreamHlsAudiobook_WritesCorrectConcatList` measured 299.42s (net9) / 299.67s
  (net10) = 84% of the class's summed wall clock (356.8s / 344.0s), in a class of
  265 filtered tests. Run in ISOLATION it takes 349ms.
- Root cause: `StreamHlsAudioSpeed_NewLaunch_KillsSameDeviceSupersededEncode_
  SparesOtherDevices` deliberately leaves TWO live fake-ffmpeg encodes alive at its
  end (device B's spared 1.75x and device A's final 2.0x; the fake's script is
  `sleep 300`), each holding one encode-gate slot (default cap 2), and the gate
  releases a slot only on the process's own exit (the 500ms exit-poll). The next
  gated test in the serialized class (the concat pin) then blocked on
  `gate.WaitAsync` until the first sleeper's 300s expired - measured 299.42s,
  matching sleep 300 to the second.
- Park attribution (the task's guessed dominant term): ServeInLockWarmCacheAsync
  has 18 call sites x 400ms = 7.2s total (4.5s at the adopted 250ms), and the
  whole 19-test park family measures 8s per filtered run. (Gate-round
  correction: the first commit message says 17/6.8s, an undercount; 18 is the
  grep-verified figure.) Settle budgets: the 20s EndpointSettleBudget and
  the production stall budgets are reached by NO test (largest honest test:
  7.14s, the JF-665 stall pin with its deliberate 5s override).

### LEVERS TAKEN / DECLINED (with margins)

1. GATE-LEAK FIX (the attribution's direct output; not in the original a/b/c list):
   the SparesOtherDevices test now kills every variant's live encode through the
   `LiveSpeedEncodeProcessForTest` registry in a finally (the same shape the
   neighboring JF-668 pin already used), drains the gate inside that finally, and
   asserts the gate refilled to its configured cap (2, exactly: the per-test Plugin
   reset pins the config default and the test's own first CreateController re-applies
   the config capacity to the static gate, rebuilding away any un-restored static
   swap; an entry-count snapshot was tried and REFUTED - it captured a prior test's
   transient exit-poll drain and red both TFMs) before returning, so no slot can
   cross the test boundary. Measured: class 5m57s -> 45s (net9) and 5m44s -> 44s
   (net10). No timing assertion touched; the spared-encode assert still runs BEFORE
   the kills.
2. (a) PARK-WINDOW REDUCTION, ADOPTED at 250ms (was a bare 400ms literal; now the
   named const `ParkWindowMs` with the failure-mode rationale in its doc).
   Measured margin: the 19-test park family (which CONTAINS the
   JF-677/JF-680/JF-681/JF-700/JF-704 pin families, all green UNCHANGED through
   every run) passed 10/10 runs at 250ms, 10/10 at 150ms, 6/6 at 100ms on this
   host, so the true pre-lock-path requirement is bounded below 100ms and the
   adopted 250ms carries a >=2.5x margin. The failure mode of a too-short window
   is a LOUD red (the JF-681 probe assert or the not-completed assert), never a
   false pass, because both asserts read the endpoint's own outcome. Family
   per-run cost 8s -> 5s; class 45s -> 42s per TFM.
3. (b) SETTLE-BUDGET SEAMS, DECLINED with evidence: no test reaches any settle
   budget (attribution above), and the seams already exist where semantics need
   them (`HlsMonitorStallBudgetOverride` on the JF-665 pins, test-local
   `EndpointSettleBudget`). A smaller default would only remove worst-case slack
   that is never paid.
4. (c) SPLIT-BY-STATIC-OWNERSHIP, DECLINED: post-fix the class is 42s of a ~2min
   suite; the lever requires opening the assembly-level DisableTestParallelization
   that guards Plugin.Instance and the other shared statics across ALL test
   classes, a new race surface for no measurable need.

### WALL-CLOCK PROOF (bird, Debug, both TFMs)

| run | BEFORE | AFTER (final state) | delta |
|---|---|---|---|
| class filter net9.0 | 5m57s (357s host / 372s wall) | 42s | -88% |
| class filter net10.0 | 5m44s (344s host / 346s wall) | 42s | -87% |
| full suite net9.0 | 7m00s (424s wall) | 1m54s (123s wall) | -73% |
| full suite net10.0 | 6m56s (422s wall) | 1m55s | -72% |

Suite counts unchanged at 5014/5014 per TFM on the final state (265/265 on the
class filter, re-run twice per TFM for flake confidence after the gate-round
assert change; the one intermediate red - the entry-snapshot variant - is
documented under lever 1).

Runs executed (workflow discipline): full suite BEFORE once per TFM, full suite
AFTER once per TFM on each of the two shipped states (the pre-gate-edit pair at
1m53s/1m54s, the pre-rework final pair at 1m54s/2m13s, and the rework-final pair
at 1m54s/1m55s that the table records), class-filter runs per measurement state,
the park-family filtered sweep (2x at the 400ms baseline, 10x at 250ms, 10x at
150ms, 6x at 100ms), and post-gate/post-rework class repeats. All other
verification was filtered class/family runs.

### GATE-MARKER REWORK ROUND (2026-10-03, second commit)

Seven findings; the three scaled axes (the teardown's cap-2 exactness, the
250ms window's false-green impossibility, the settle-budget refutation) were
verified sound end-to-end. Dispositions, all landed in the second commit:

- F6/F7/F3 applied (trivial): the pid declaration moved inside the try (the
  finally never reads it); the attribution's call-site count corrected to 18
  (7.2s at 400ms, 4.5s at 250; the first commit message keeps its undercount
  of 17 and says so here); ParkWindowMs's doc names the slow-host false-red
  cause and its remedy (raise the constant, never loosen the asserts).
- F1 applied: per-launch tripwire. Each StreamHlsAudioSpeed launch is wrapped
  in LaunchWithinAsync (CompletedWithinAsync, 30s budget: above the endpoint's
  own ~20s first-segment ceiling, an order of magnitude below one sleep-300
  lifetime), so a supersede-kill ordering regression that queues a launch
  behind the full gate REDS at 30s instead of passing silently ~300s later.
- F4 applied: the kill-through-registry idiom hoisted to KillLiveEncode, now
  the shared shape for both the JF-730 finally and the JF-668 finally.
- F2 applied: pid-file kill backstop (KillEncodeByPidFile) for the corner an
  encode faulting between process start and registration escapes the
  registry; the fake writes its pid first, so the files cover that window.
  The theoretical recycled-pid kill hazard is documented in the helper's doc
  (64-bit pid_max makes a wrap within one run effectively impossible; a
  shebang script has no stable owner handle to check).
- F5 FILED as JF-731: the Dispose-level class backstop (kill every registry
  entry + drain the gate once per test) that would make the whole class
  immune in one place and turn both per-test finallys into redundancy.

The rework's own code-review round (high, incremental diff) found the first
tripwire draft defective in exactly the disease's shape and all six findings
were applied: the drain is now a RE-ARMING kill-and-poll loop (a one-shot
kill pass ran before a tripwire-abandoned zombie could acquire a freed slot
and spawn, re-stranding the class; the loop kills the zombie the moment its
registry entry or pid file appears and the drain it delays is the drain that
kills it), the budget raised 10s -> 30s to clear the endpoint's own ceiling,
the result unwrapped with await (no AggregateException wrapper on endpoint
faults), the helper composed onto TestHelpers.CompletedWithinAsync, the
pid-kill hazard documented, and the ParkWindowMs residual sentence
un-contradicted (slow host = the endpoint walks past the window, the plant
lands mid-walk, the fast path serves warm, the probe reds).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (verified 2026-10-03 final state: `Build succeeded`, 0 errors)
- [x] #2 dotnet test passes (verified 2026-10-03 final state: 5014/5014 net9.0, 5014/5014 net10.0; class filter 265/265 both TFMs, twice)
- [x] #3 No new compiler warnings introduced (verified by forced-recompile recount after every edit round: the only warnings are the 2 pre-existing xUnit1030 at the untouched ObserveStrandedEndpointAsync ConfigureAwait site, present on the baseline build)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (n/a: no session-attribute surface touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (n/a: no HttpClient surface touched)
- [x] #6 NLU test fixtures updated if interaction model changed (n/a: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (n/a: test-only change, no handler logic)
- [x] #8 Locale response strings added to all 17 locales (n/a: no locale strings touched)
- [x] #9 /simplify passed (no blocking cleanups remaining) (4-angle round: reuse clean; efficiency clean; simplification 3/3 applied + the round surfaced an unintended sed collateral, ObserveStrandedEndpointAsync 150ms->100ms, restored; altitude 1 applied / 1 refuted with production evidence / 1 skipped at the two-copy tolerance edge)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked) (first diff: 6 findings, 4 applied incl. the hardening of the gate-refill target whose first suggested form, entry capture, was empirically refuted, 2 skipped with recorded reasons; the coordinator's gate-marker rework round: 7 findings, 6 applied, 1 filed as JF-731; the rework diff's own /code-review high: 6 findings, all applied)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle including an API-death recovery and a rework round: worker commits 41142f65 + 2229f537, merged as 66b33a7f. The suite wall-clock lever delivered by attribution, not guesswork: the task's premise (the parks) was REFUTED by TRX measurement - the class's cost was ONE test blocked 299s on the encode gate behind two deliberately spared sleep-300 fakes. The SparesOtherDevices teardown now kills every live encode through the hoisted KillLiveEncode registry helper (the JF-668 twin unified) with a KillEncodeByPidFile backstop, drains in a re-arming kill-and-poll loop, and refills to the configured cap; a per-launch 30s tripwire converts the supersede-kill ordering regression from a silent ~300s green into a red; the park window 400ms -> ParkWindowMs=250 with measured margins (10/10 at 250 and 150, 6/6 at 100; no false-green mechanism; the slow-host false red documented with its remedy). Wall-clock: full suite 7m00s -> 1m54s net9 (-73%), 6m56s -> 1m55s net10 (-72%); class -88%. The worker survived a glm-route corrupted-thought-signature death (resumed from transcript), and its rework gate refresh then caught the tripwire's own zombie-window flaw (a one-shot kill pass before an abandoned zombie could spawn its own sleep) - fixed with the re-arming drain. JF-731 filed (the Dispose-level class backstop). Suites: worker 5014/5014 both TFMs at the new speed, class 265/265 twice per TFM, and the merged-tree run itself 5019/5019 both TFMs exit 0 at 1m57s/2m11s - the dividend compounding its own verification. Test-only: no production surface, no deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
