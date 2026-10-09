---
id: JF-801
title: >-
  JF-801 - wall-clock-asserting tests (RetryHelper budget pin first) now run
  contended under the JF-792 parallel phase; margins sized for solo execution
status: Done
assignee: []
created_date: '2026-10-06 18:43'
updated_date: '2026-10-09 23:06'
labels:
  - test-infrastructure
  - flakiness
dependencies: []
references:
  - >-
    backlog/tasks/jf-792 -
    VideoAudioControllerTests-regrew-to-roughly-two-thirds-of-suite-wall-clock-after-the-windowing-waves-re-evaluate-the-declined-partition-lever.md
  - Jellyfin.Plugin.AlexaSkill.Tests/Unit/RetryHelperTests.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed by the JF-792 worker (2026-10-06) from the /code-review high round. The JF-792 parallelism toggle moved the uncollected wall-clock-asserting tests from solo serialized execution into the contended parallel phase, where up to ProcessorCount collections compete for the scheduler and continuations slip:

- Jellyfin.Plugin.AlexaSkill.Tests/Unit/RetryHelperTests.cs:544 Sync_AlwaysTransient_StopsWithinTimeoutBudget (elapsed < 6000ms budget + 1000ms margin). This is the CLAUDE.md-designated SOLE guard for the Alexa ~8s response window (JF-358/JF-359); a flake here triggers false regression hunts.
- DoubleMetaphoneTests.cs:242 (< 500ms) and FuzzyMatcherTests.cs:534 (< 2000ms).

Local counter-evidence at filing: 3 consecutive green full-suite runs both TFMs on the quiet 8-core dev box, plus the JF-792 flake gate (10 runs). Exposure: CI runners (github-hosted 2-4 vCPU) are far more contended than the dev box; the margins were sized pre-toggle. Loosening the bounds is an assertion change and was out of scope for JF-792 (arrange-only), so the disposition is this filing.

Candidate shapes when taken: (a) exempt just these three classes from the parallel phase (a dedicated DisableParallelization collection "TimingSolo"), keeping the rest of the parallel win; (b) re-measure the margins under deliberate parallel load and resize them with red-proof evidence; (c) make the RetryHelper budget test read the budget from the same constant the production code uses and assert on the RELATIVE overshoot rather than an absolute wall clock. Deciding needs a reproduction on a contended runner first (CI log or a taskset-limited local run).
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
PRESERVATION NOTE (2026-10-09 23:10, orchestrator): the two records below previously sat as raw tails OUTSIDE the managed sections; the backlog-MCP status edit on main dropped them from the main checkout (the known tail-truncation gotcha, backlog_mcp_worktree_interaction), and the jf801 merge would have silently kept the truncated side. Restored from the branch and folded INTO this managed section verbatim so MCP roundtrips cannot lose them again.

GATE-MARKER TAIL UPDATE (2026-10-06): the roster was INCOMPLETE and the cheap mitigation is LANDED, ahead of any reproduction, per the marker's cost-benefit (an intermittent main red costs a triage round; the exemption costs seconds). Completed roster, all now in the new [Collection("TimingSolo")] (DisableParallelization; runs exclusively after the parallel phase): RetryHelperTests (the budget pin), DoubleMetaphoneTests (the 500ms encode-throughput margin over 10K encodes, plausibly exceeded on a 4-vCPU runner with CPU-bound siblings), DeviceQueueManagerTests + KeyedOneShotDebounceTests + AudiobookPositionTrackerTests (the JF-449 park family's POSITIVE 2s waits - started.Wait(2s) and the post-release WaitAsync(2s) - which the 60s park-bound fix left tighter than the 5s bound that already false-reded); plus SafeExitCode_LiveProcess_ReturnsMinusOne returned from the pure parallel class to the serialized Plugin collection (its 2s process-liveness margin is a wall-clock assertion; contention between Process.Start and the check lets the shell exit first). The TimingSolo definition lives beside PluginCollection with the rationale. RESIDUAL for this task: the classes NOT exempted that still carry wall-clock sensitivity below the demonstrated starvation bound (audit them if a contended red ever appears; the IL roster guard JF-800 covers the parallel-phase membership contract, not timing).

SECOND INCIDENT FAMILY ADDENDUM (2026-10-07, from the JF-807 marker review; cross-ref JF-809): the flake surface this task owns is not only wall-clock. ReminderLocaleStringsTests.ReminderSetRelativeFor_ResolvesAllLocales (JF-809: one failure in a full net10.0 parallel run, class alone 30/30, two later full runs green, message lost) repeats the PersonalizedGreetingLocaleTests.WelcomePersonalized en-US shape recorded on the b41a588e tail: both are pure ResponseStrings.Get invariant theories, both classes are uncollected and therefore run in the parallel phase, and both failures were one-shot with the message never captured. Shared-shape hypothesis, nameable from source: ResponseStringsTests (also uncollected, parallel phase) calls ResponseStrings.Reset() seven times; Reset clears _locales and drops _initialized under the lock, but a concurrent Get that already passed EnsureInitialized can read a cleared dictionary and fail soft to the raw KEY, which is exactly what the Assert.NotEqual(key, value) theories catch. Mechanism hunt for the next taker: serialize the Reset-calling tests against the locale-strings readers (one shared collection), or make Reset's teardown exclusive with Get; the TimingSolo wall-clock exemptions do not cover this shape, and both recorded incidents fit it without residue.

COMPLETION RECORD (2026-10-09 23:10, worker d2524de5 on branch jf801, merged by the orchestrator; full evidence in the commit message). SCOPE FINDING: at HEAD the mitigation was already 2/3 landed (e5c80f89 collected RetryHelperTests + DoubleMetaphoneTests + the JF-449 park family into TimingSolo); this task closed the remaining gap, FuzzyMatcherTests (the third class the filing itself names): bare [Collection("TimingSolo")] + one roster clause in PluginCollection.cs; no production code changed; the RetryHelper assertion byte-identical. REPRODUCTION (the honest result): NO red in 6 deliberate contention attempts (taskset CPU limits + parallel loop loads, up to the full-suite + 8-loops incident shape, 5606/5606 green there); nearest miss DoubleMetaphone 408ms = 82% of its 500ms bound (24x stretch, 5x variance); the only ever-observed starvation (JF-449, >5s) was under swap thrash NOT reproduced deliberately on the shared box; decision taken on design grounds per the filing's own fallback (the three-class roster completes the TimingSolo contract; margin resize REJECTED as pin-weakening; shape (c) REJECTED as already satisfied in substance: the pin is relative-shaped and the 6000ms value is independently pinned by AlexaRequestTimeoutMs_ValueIs6000). MECHANISM DEMONSTRATED: post-fix contended re-run, identical shape: the drained phase collapses the pins near solo (DM 408->95ms, FM 1000->588ms). BUDGET-PIN TEETH red-proof (scratch mutations of IsBudgetExceeded, both reverted): budget honored at 10x scale -> pin FAILED in 8s ('ran 8207ms, exceeding the 1500ms budget'); budget fully ignored -> uncapped backoff never completes (killed at 300s vs ~1s healthy = guaranteed CI failure). Process note kept honest: the worker's first attempt at mutation (2) ran a stale DLL via --no-build (caught because the arithmetic matched the previous mutation exactly), redone fresh. SECOND-INCIDENT CLOSURE: the JF-809 ResponseStrings.Reset race named in the addendum below is CLOSED at HEAD: 607b9466 (the JF-807 review tail) collected ResponseStringsTests into [Collection("Plugin")] and a project-wide grep shows zero other Reset()/RegisterLocale() callers, so no writer to _locales remains in the parallel phase. Verifiers: Release build 0 warnings 0 errors; full suite 5606/5606 BOTH TFMs on the final tree; the orchestrator's merge-identity check (git diff jf801 HEAD -- '*.cs' = empty) proves the merged C# tree is byte-identical to the worker's tested tree. Gates: /simplify (4 agents; the stale-on-arrival comment enumeration APPLIED to the one-ledger-clause form; efficiency CLEAN, altitude right-depth) + /code-review high (code CLEAN across 8 angles; F1 the truncated-tail rescue = this preservation note; F2 = this record; F4 the PerfGuard double-print observation filed as its own task). Status flip to Done rides the orchestrator's final-diff gate pass with the night's batch.
<!-- SECTION:NOTES:END -->
