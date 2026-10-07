---
id: JF-801
title: >-
  JF-801 - wall-clock-asserting tests (RetryHelper budget pin first) now run
  contended under the JF-792 parallel phase; margins sized for solo execution
status: To Do
assignee: []
created_date: '2026-10-06 18:43'
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

GATE-MARKER TAIL UPDATE (2026-10-06): the roster was INCOMPLETE and the cheap mitigation is LANDED, ahead of any reproduction, per the marker's cost-benefit (an intermittent main red costs a triage round; the exemption costs seconds). Completed roster, all now in the new [Collection("TimingSolo")] (DisableParallelization; runs exclusively after the parallel phase): RetryHelperTests (the budget pin), DoubleMetaphoneTests (the 500ms encode-throughput margin over 10K encodes, plausibly exceeded on a 4-vCPU runner with CPU-bound siblings), DeviceQueueManagerTests + KeyedOneShotDebounceTests + AudiobookPositionTrackerTests (the JF-449 park family's POSITIVE 2s waits - started.Wait(2s) and the post-release WaitAsync(2s) - which the 60s park-bound fix left tighter than the 5s bound that already false-reded); plus SafeExitCode_LiveProcess_ReturnsMinusOne returned from the pure parallel class to the serialized Plugin collection (its 2s process-liveness margin is a wall-clock assertion; contention between Process.Start and the check lets the shell exit first). The TimingSolo definition lives beside PluginCollection with the rationale. RESIDUAL for this task: the classes NOT exempted that still carry wall-clock sensitivity below the demonstrated starvation bound (audit them if a contended red ever appears; the IL roster guard JF-800 covers the parallel-phase membership contract, not timing).

SECOND INCIDENT FAMILY ADDENDUM (2026-10-07, from the JF-807 marker review; cross-ref JF-809): the flake surface this task owns is not only wall-clock. ReminderLocaleStringsTests.ReminderSetRelativeFor_ResolvesAllLocales (JF-809: one failure in a full net10.0 parallel run, class alone 30/30, two later full runs green, message lost) repeats the PersonalizedGreetingLocaleTests.WelcomePersonalized en-US shape recorded on the b41a588e tail: both are pure ResponseStrings.Get invariant theories, both classes are uncollected and therefore run in the parallel phase, and both failures were one-shot with the message never captured. Shared-shape hypothesis, nameable from source: ResponseStringsTests (also uncollected, parallel phase) calls ResponseStrings.Reset() seven times; Reset clears _locales and drops _initialized under the lock, but a concurrent Get that already passed EnsureInitialized can read a cleared dictionary and fail soft to the raw KEY, which is exactly what the Assert.NotEqual(key, value) theories catch. Mechanism hunt for the next taker: serialize the Reset-calling tests against the locale-strings readers (one shared collection), or make Reset's teardown exclusive with Get; the TimingSolo wall-clock exemptions do not cover this shape, and both recorded incidents fit it without residue.
