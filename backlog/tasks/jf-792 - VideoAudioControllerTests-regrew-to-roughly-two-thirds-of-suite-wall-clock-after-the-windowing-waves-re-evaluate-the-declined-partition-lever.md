---
id: JF-792
title: >-
  JF-792 - VideoAudioControllerTests regrew to roughly two thirds of suite wall
  clock after the windowing waves; re-evaluate the declined partition lever
status: Done
assignee: []
created_date: '2026-10-06'
labels:
  - test-infrastructure
  - performance
dependencies:
  - JF-730
priority: medium
---

## Description

## Definition of Done

## Final Summary

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

Filed 2026-10-06 by the JF-730 re-measurement worker (certification pass, no
code changes; the full dated table lives in the re-measurement appendix of the
JF-730 task file). The JF-730 close declined lever (c), the
split-by-static-ownership partition, on the premise "post-fix the class is 42s
of a ~2min suite ... a new race surface for no measurable need". That premise
no longer holds.

### Tonight's attribution (bird, Debug, tree at 71feb0ba, TRX per-test durations)

- The class is 299 tests measuring 74.1s of a 114.1s summed suite on the clean
  net10 leg (65%) and 97.6s of 172.8s on the contended net9 leg (56%); the
  next-largest class is SmapiTokenRefresherTests at 4.0s. The class is ~25x
  the next class on both TFMs.
- Class filter: 1m16s net9 / 1m14s net10, both green 299/299, against 42s/42s
  (265 tests) at JF-730 close. Growth driver: +34 tests from the JF-778
  windowing wave and the JF-784/785 concat-scope work, and they skew slow.
- NO pathology: the slowest test is still the documented JF-665 stall pin
  (7.4s net9 / 7.1s net10 against 7.14s at close; the 5s
  HlsMonitorStallBudgetOverride verified at its source site). The cost is
  distributed: 17 tests at >= 1s (38.6s contended; gate-slot waits, monitor
  stall budgets, supersede kills), 62 tests in the 400ms to 1s band (40.2s;
  fake-ffmpeg process lifecycle waits incl. the production 500ms exit-poll
  granularity), 178 tests under 150ms (8.4s).

### Candidate fix shapes (risk order; all assertion-neutral per the JF-751/JF-760 arrange-only discipline)

1. THE PARTITION (the re-evaluation this task owns): remove the assembly-level
   `DisableTestParallelization` (PluginCollection.cs), put every static-touching
   test class into an explicit serialized collection (187 of 313 classes already
   carry `[Collection("Plugin")]`; the remaining ~126 need the static-usage
   audit: Plugin.Instance, QueueContinuationStore, RadioModeState,
   PlaybackReportOrdering, the encode registries, the cache root), then split
   VideoAudioControllerTests into families grouped by WHICH statics they touch
   (encode-gate family; cache-root family; position-tracker family; the pure
   token/playlist families) so within-group serialization is preserved while
   the groups run in parallel. Blast radius is the audit plus the risk a missed
   static races; mitigations: the JF-730 statics list above, and repeated
   full-suite runs (>= 5 per TFM) as the flake gate before merge.
2. PARK-FLOOR RIDER (small, only alongside 1 or as a standalone if 1 is
   declined again): the 18 park sites x ParkWindowMs=250ms cost 4.5s; JF-730
   measured the family green 10/10 at 150ms and 6/6 at 100ms, so lowering to
   150ms saves ~1.8s with the documented margin. Re-opens the slow-host margin
   decision for a ~2% class gain; do not take it alone.
3. DECLINED UPFRONT (recorded so the next pass does not re-derive): the
   production 500ms exit-poll granularity and the monitor stall budgets are
   production constants outside test reach, and JF-730 already declined the
   settle-budget seams with evidence (no test reaches any settle budget).

### Acceptance

- Wall-clock table before/after, both TFMs, contention caveat recorded (any
  parallel worker inflates absolute numbers; relative attribution only).
- Full suite green both TFMs, repeated (>= 5 consecutive clean runs per TFM as
  the race/flake gate if the partition is taken).
- Zero assertion changes; the JF-677/JF-680/JF-681/JF-700/JF-704 pin families
  green unchanged; the WarmingGateCoverageTests roster untouched.

## Work log (JF-792 worker, 2026-10-06)

### 1. Static-usage audit of the 125 uncollected classes (census at main tip 1ae7b408: 312 test classes, 187 collected, 125 uncollected)

Mechanism: per-class body scan (class extents by brace matching) for the shared
statics markers (Plugin.Instance, Plugin.ResetInstance, EnsurePluginInstance,
EnsureRealPlugin, PluginTestBase inheritance, the Swap* scopes,
ConfigSerializerMock, QueueContinuationStore, RadioModeState,
PlaybackReportOrdering, StubBaseItemStatics/BaseItem.LibraryManager writes,
VideoAudioController statics, SetEnvironmentVariable, CurrentCulture writes,
HlsMonitorStallBudgetOverride), plus targeted follow-ups (all 15 `new Plugin(`
sites mapped to enclosing classes; HttpClient users inspected for real sockets;
fixed-path/GetTempPath users inspected; `static` field declarations in
uncollected classes read).

Findings, by verdict:

- DIRTY (1): `Handler/DeadMicSweepElicitTests` inherits `PluginTestBase`; its
  per-test ctor writes `Plugin.ResetInstance()` + clears
  QueueContinuationStore/RadioModeState/PlaybackReportOrdering (shared
  statics). Collection-contract drift (the PluginTestBase doc demands
  membership). Fix: `[Collection("Plugin")]`.
- CLEAN, benign-static registry only (9): classes whose only shared touch is
  `CreateRegisteredTempDir` (ShuffleIntentHandlerTests, DeviceQueueManagerTests,
  AudiobookPositionTrackerTests, LastPlayedResponseInterceptorTests, and the
  VideoAudio split candidates covered below). Evidence: the registry is
  `PluginTempDirSweeper._dirs`, a `ConcurrentBag<string>.Add`
  (PluginTempDirCleanup.cs), GUID-unique dirs, swept once at ProcessExit;
  additive concurrent registration cannot race.
- CLEAN, self-contained static override (1): `Unit/SmapiManagementWiringTests`
  sets `SmapiManagement.RawModelClientOverrideForTests` in the ctor and nulls
  it in Dispose; it is the ONLY user of that seam in the assembly (grep), no
  other parallel-phase class constructs SmapiManagement or reads the seam, and
  the Plugin collection (all other Smapi users) runs exclusively after the
  parallel phase, so the override is never visible to another class.
- CLEAN, class-internal static (1): `Unit/CatalogManagerPollingTests` mutates a
  private static `_currentPollBody` consumed by its own per-test handler;
  within one class xUnit never runs tests concurrently (verified at source,
  v2-2.7.0 TestCollectionRunner.RunTestClassesAsync is a sequential foreach),
  and the field is invisible outside the class.
- CLEAN, read-only shared surfaces: `WarmingGateCoverageTests` and the IL/roster
  census classes (IlCallScannerTests, the *RosterTests families,
  AskFirstMatchCallerCensusTests, AudioPlayerPlayConstructionRosterTests) scan
  plugin-assembly IL read-only; locale/manifest/model classes
  (ResponseStringsTests, LocaleStringsTests, CatalogWiringLocalesTests,
  InteractionModelTests, ManifestSkillTests, CreateSkillInteractionModelTests,
  TestLocales) read the checked-in JSONs read-only.
- CLEAN, no sockets despite HttpClient in file: LibrarySyncServiceTests,
  LiveTvStreamResolverTests, ModelDeploymentManagerTests, CatalogManagerTests,
  CatalogManagerPollingTests all wrap mock/stub HttpMessageHandlers
  (RecordingHandler/FakeSmapiHandler/StubHttpClientFactory); TestConnectionTests
  is pure Uri.TryParse.
- CLEAN, no markers at all (111): the remaining pure unit classes (phonetic
  synonym generators, matchers, slot/token helpers, config-defaults classes in
  mixed files, LWA DTO classes, etc.). `new Plugin(` appears in zero
  uncollected classes (all 15 sites mapped into collected classes or
  TestHelpers itself).

Residual risk class (not a static, order-dependence): uncollected classes that
exercise handler paths reading `Plugin.Instance?.Configuration` may have been
passing on a LEFTOVER instance from a previously-run collected class; in the
new ordering the parallel phase runs first, so they see null throughout. All
such reads are null-tolerant with default-config behavior identical to a
freshly-ensured default instance, but the empirical red list from the first
post-toggle run is the arbiter (that run is the next step).

### 2. Parallelism semantics (read at source, xunit v2-2.7.0, the version in the csproj)

From `XunitTestAssemblyRunner.RunTestCollectionsAsync` (fetched from the
v2-2.7.0 tag): collections WITHOUT DisableParallelization are started together
on the MaxConcurrencySyncContext (default max = ProcessorCount, 8 here);
execution then AWAITS ALL of them before running the DisableParallelization
collections one at a time. From `TestCollectionRunner.RunTestClassesAsync`:
classes inside one collection run in a sequential foreach. Consequences that
shape the design: (a) the Plugin collection stays sequential internally AND
never overlaps the parallel phase, so parallel classes cannot race any
collected class; (b) multiple exclusive collections only serialize against
each other (no win), so split families must be plain parallel collections; (c)
all parallel-phase writers of Plugin.Instance must share ONE collection
(within-collection classes serialize) or be eliminated.

### Baseline (this box, quiet, Debug, worktree at 50aaad80 + audit edits)

- net9.0: 5418/5418 green, test Duration 1m40s (VSTest), dotnet-test wall 151s
  (fresh-worktree first build included).
- net10.0: 5418/5418 green, test Duration 1m38s (VSTest), dotnet-test wall 155s
  (warm build).

### 3. The toggle (commit dd46b979)

The attribute lived at `Jellyfin.Plugin.AlexaSkill.Tests/PluginCollection.cs`
line 14 (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`);
there is NO xunit.runner.json and no csproj PropertyGroup for it. Removed, with
the replacement comment stating the verified execution model. DeadMicSweepElicitTests
(the audit's one dirty uncollected class) joined `[Collection("Plugin")]` in the
same change. First post-toggle full run: GREEN 5418/5418 net10 with NO red list
(the static audit had predicted this: after collecting DeadMicSweepElicitTests,
zero parallel-phase writers of Plugin.Instance or the PluginTestBase statics
remain, so no order-dependence red surfaced either). Timing: net10 1m33s
(baseline 1m38s): the ~5s is the parallel-phase compression of the ~124
uncollected classes; the exclusive Plugin collection then dominates.

### 4. The split (commit dd46b979), with the measured lever

Per-test durations (TRX, class filter, net10): the class sums to 72.6s; the
transitively-static-free families measure 0.94s (66 tests); the token-bearing
non-encode families ~5.3s (25 direct + 7 audiobook tests that mint scoped
tokens through tuple-returning helpers); the encode mass is 66.0s (119 tests).
Split shipped: `VideoAudioControllerTestHarness` (per-instance fixture: fields,
temp-dir/cache ctor, virtual dir-delete Dispose, the W4 denied-directory helper
+ FileMode consts) + `VideoAudioControllerPureTests` (the 66 static-free tests,
NO collection attribute: parallel phase) + the pruned serial
`VideoAudioControllerTests` ([Collection("Plugin")] kept, PluginTestBase resets
folded verbatim into the ctor, Dispose override calling base.Dispose() in the
same position as the old inline delete). Move-set rule: transitive closure over
the file's call graph must avoid Plugin.Instance / the shared _config /
StreamTokenHelper minting (any CreateController call site with a non-null first
arg) / the encode machinery / the ServeInLock IL pins (which pin
typeof(VideoAudioControllerTests) and keep their helper family in place).
Verification: filtered run 300/300 both TFMs (234 serial + 66 pure cases: no
test lost or added); full suite 5418/5418 both TFMs.

WHY THE 66s ENCODE MASS STAYS (the recorded semantic-risk boundary): moving any
encode-carrying family into the parallel phase requires (a) dropping the JF-731
Dispose backstop for that family (its unconditional gate-drain waits for the
CONFIGURED capacity to be free process-wide; a sibling family's legitimate
in-flight slot reads as a stuck slot and false-reds, and its global
live-registry kill half would kill another family's encode), and (b) accepting
cross-family contention on the shared static gate semaphore that several tests
assert on (EncodeGate_*, TwoConcurrentTranscodes_SecondWaitsForTheSlot,
MusicStartProceedsWhileTranscodesOccupyTheTier), and (c) parallel
Plugin.Instance ownership for the token secret. All three are assertion-bearing
or assertion-adjacent, i.e. outside the zero-assertion-change constraint: those
families stay, per the task's "a family that cannot move without semantic risk
stays, recorded why".

Post-split full suite: net9 1m31s, net10 1m33s. The lever is measured and
nearly exhausted at its ceiling: the parallelizable mass inside this class was
0.94s; the observed end-to-end saving over baseline (toggle+split combined,
sequential-vs-parallel phases) is net9 9s / net10 5s, and the remaining
critical path is the serialized Plugin collection (the 66s encode mass plus
the other collected classes), which the JF-731 backstop pins in place.

### 4a. Corrected split arithmetic (code-review F3; the commit messages mixed methods and cases)

Final composition, verified by the reviewer at method, attribute, and case
level: VideoAudioControllerTests 173 test methods, all [Fact] (173 cases);
VideoAudioControllerPureTests 67 test methods (55 [Fact] + 12 [Theory] whose
72 InlineData rows expand to 127 cases); 173 + 127 = 300 cases, zero lost,
added, or duplicated against the pre-split class. The pure class runs
127/127 green in ~1s wall on net10.

### 5. Review gates

/simplify (4 parallel agents: reuse, simplification, efficiency, altitude):
9 applied (the PluginTestBase reset sequence hoisted to ONE owner
ResetSharedStatics(); three stale/orphaned section headers deleted; the fourth
SafeExitCode sibling moved to the pure class, its STAY verdict having been a
classifier false positive via a Dispose name collision; 54 surplus blank lines
collapsed; 3 dead usings removed; the PluginTestBase and PluginCollection doc
contracts updated to the post-toggle model; the serial class doc carries the
routing rule), 3 reasoned skips recorded in the commit (cache-fixture second
copy below the JF-713 trigger; leaner statics-only base rejected on parity and
single-membership-rule grounds; pre-existing redundant using out of scope).
Zero actionable efficiency findings (per-test cost strictly decreased for every
moved test). One filing: JF-800 (the parallel-phase contract deserves the
WarmingGateCoverageTests-style IL roster guard; a new assertion surface was out
of the arrange-only scope).

/code-review high: the diff verified mechanically clean (move integrity
byte-identical; the xunit execution model confirmed at tag v2-2.7.0 including
the corrected method names RunTestCollectionsAsync / RunTestClassesAsync;
contract-drift scan: every PluginTestBase-derived test class carries
[Collection("Plugin")], no uncollected Plugin.Instance writer, no fixed paths,
ports, culture writes, or SetCurrentDirectory in any uncollected class; every
production static reachable from the moved tests span-scanned shared-state-free).
3 findings, 3 dispositioned: F1 FILED as JF-801 (the wall-clock-asserting
uncollected tests, above all RetryHelperTests.Sync_AlwaysTransient_StopsWithinTimeoutBudget,
now run contended in the parallel phase; margins were sized for solo execution;
CI runners are 2-4 vCPU vs this quiet 8-core box); F2 APPLIED (the execution-model
comment cited a nonexistent method); F3 APPLIED here (the arithmetic correction
above).

Residual-risk register for the parallel phase, post-review: (1) JF-801's
timing-margin exposure on contended runners; (2) the JF-772 episode-audio
timing-blip family was NOT observed in any of this task's full runs (both
TFMs), including under parallel load; (3) the prose-only parallel-phase
contract until JF-800's guard lands.

### 6. Flake gate, first attempt: one red, fixed assertion-neutrally, count restarted

Gate run 1 (net10): 1 failed of 5418,
`Playback.DeviceQueueManagerTests.Dispose_TearsDownDebounce_BeforeFinalFlush`
("final flush ran before the debounce teardown (old Dispose order)"), the test
clocked at 10s inside a 2m28s suite run (the parallel phase saturating all 8
lanes). Mechanism, read at source: the JF-449 park harness parks the persist
callback inside `KeyedOneShotDebounce.RunCallback` (which holds `_gate` across
`BeforeCallbackGate`) with a BOUNDED `release.Wait(5s)`; under thread starvation
the wait expired, the parked callback ran its write, and the ordering witness
observed the queue file early. A harness false positive, not a Dispose-order
regression: the pin is green in isolation and in every other full run. Fix
(arrange-side, zero Assert lines changed, commit 93a34711): the park bound
widens 5s -> 60s at all five `BeforeCallbackGate` sites sharing the idiom
(DeviceQueueManagerTests x2, AudiobookPositionTrackerTests x1,
KeyedOneShotDebounceTests x2; all three classes run in the parallel phase),
reason commented at each site. The wait still returns the instant `release`
fires (~150ms on the normal path); the bound only lifts the starvation ceiling.
Touched-class filtered runs 93/93 green both TFMs. The gate count restarted
from zero on this state.





### 7. Flake gate, final state (commit 93a34711): PASSED 10/10

5 consecutive green full-suite runs per TFM, 5418/5418 each:
- net10.0: 1m43s, 1m55s, 1m38s, 1m38s, 1m38s
- net9.0: 1m42s, 1m38s, 1m38s, 1m41s, 1m37s

### 8. Final timing table (VSTest test-run Duration; this box, Debug, 8 cores)

| State                                   | net9.0 | net10.0 |
|-----------------------------------------|--------|---------|
| baseline (sequential, pre-toggle)        | 1m40s  | 1m38s   |
| post-toggle (parallel on, no split)     | (not run) | 1m33s |
| post-split (toggle + 66-test move)      | 1m31s  | 1m33s   |
| final state, 5-run gate range (median)  | 1m37-1m42s (1m38s) | 1m38-1m55s (1m38s) |

Honest reading: in the quieter measurement windows the combined lever measures
5-9s (5-9%); under back-to-back gate load the median converges to the baseline
(net10 delta 0s, net9 delta 2s). The parallel wall clock is the MAX over lanes,
and the critical path is the serialized Plugin collection in every condition:
VideoAudioControllerTests' 66.0s encode mass plus the other collected classes.
The static audit table (section 1 rollup): of the 125 uncollected classes,
1 DIRTY (DeadMicSweepElicitTests, collected), 4 benign-registry only
(ShuffleIntentHandlerTests, DeviceQueueManagerTests,
AudiobookPositionTrackerTests, LastPlayedResponseInterceptorTests), 2
self-contained statics (CatalogManagerPollingTests, SmapiManagementWiringTests),
118 otherwise clean (the section-1 sub-verdicts: read-only IL/locale-file scans,
mocked-HTTP classes, and the plain no-marker unit classes).

VERDICT on the re-evaluated lever: the partition lever is measured and EXHAUSTED
at its arrange-only ceiling. The JF-730-era premise "no measurable need" was
wrong about the class size (74s of a ~114s summed suite is real) but RIGHT about
the partition: only 0.94s of that class is static-free and moveable without
touching assertions. The toggle itself is kept (correctness-neutral after the
audit, mild gain, restores standard xUnit behavior); the split is kept (67
tests, honest though small). This is the null-result close the task's
constraints anticipated, with the numbers above.

ALTERNATIVE LEVERS left on file (not taken; the park-floor rider stays
do-not-take-alone per the filing):
1. The 66s encode mass could parallelize only by redesigning the JF-731
   Dispose backstop (per-family gate ownership or a family-scoped drain) and
   giving parallel families their own token-secret story; both are
   assertion-bearing changes, a scope of their own.
2. The REVERSE audit: some of the 187 collected classes may be static-free and
   could LEAVE the Plugin collection (the mirror of this task's audit); worth
   ~the uncollected share of ~16s at best.
3. JF-800 (the mechanical roster guard) and JF-801 (the contended timing
   margins) are filed.

- [x] dotnet build passes with 0 errors (both TFMs, Debug; Release -warnaserror
      0 warnings 0 errors verified on the final state)
- [x] dotnet test passes (5418/5418 both TFMs; flake gate 10/10 after the one
      fixed-and-restarted red)
- [x] No new compiler warnings introduced (0 Warning(s) on every build this
      task ran, both configs)
- [x] #4 N/A: no session attributes or serialization touched (test-infra only)
- [x] #5 N/A: no HttpClient usage introduced or modified
- [x] #6 N/A: no interaction-model change (no NLU fixtures touched)
- [x] #7 N/A by worker split: no new intent or handler logic (test-infra task;
      E2E suites unaffected by collection/attribute changes)
- [x] #8 N/A: no user-facing strings, no locale files touched
- [x] /simplify passed (4 agents; 9 applied, 3 reasoned skips recorded in the
      commit; one filing JF-800)
- [x] /code-review high passed (diff verified mechanically clean; 3 findings:
      1 filed as JF-801, 2 applied)

JF-792 re-evaluated the declined partition lever with measurement and took it
to its arrange-only ceiling. (1) The static-usage audit of all 125 uncollected
classes found exactly one dirty class (DeadMicSweepElicitTests, PluginTestBase
resets; collected), with every other uncollected class clean or benignly
registered; the code-review round independently re-verified the contract
(every PluginTestBase-derived test class carries the collection attribute; no
uncollected Plugin.Instance writer). (2) The assembly-level
DisableTestParallelization (PluginCollection.cs line 14; no runner json
existed) was removed after reading the exact xunit v2-2.7.0 scheduling source:
parallel collections run first (ProcessorCount-bounded), exclusive collections
run after all of them, classes within a collection are sequential. First
post-toggle run green with no red list. (3) VideoAudioControllerTests split by
static ownership: 67 static-free tests (127 cases) moved to the parallel-phase
VideoAudioControllerPureTests on a new VideoAudioControllerTestHarness base;
173 serial cases keep [Collection("Plugin")] with the PluginTestBase resets
(hoisted to ONE owner after review) and the JF-731 backstop; byte-identity of
every moved and kept body verified mechanically. The moveable mass measured
0.94s of the class's 72.6s; the 66.0s encode mass stays serialized because
moving it requires weakening leak assertions and the shared gate, recorded as
the semantic-risk boundary. (4) The flake gate surfaced one real parallel-load
false positive (the JF-449 park harness's 5s release wait expiring under
8-lane starvation in DeviceQueueManagerTests.Dispose_TearsDownDebounce_
BeforeFinalFlush); fixed assertion-neutrally (5s -> 60s park bound at all five
BeforeCallbackGate sites), count restarted, final 10/10 green both TFMs.
(5) Wall-clock verdict, honestly: the lever delivers 5-9s in quiet windows and
converges to baseline under sustained load; the serialized Plugin collection
dominates every condition; the null-result close with the table is the
legitimate outcome the filing anticipated. Filings: JF-800 (mechanical
parallel-phase guard), JF-801 (contended timing margins). Commits in the
worker worktree: 50aaad80 (In Progress), dd46b979 (toggle + split),
6f0fbc72 (simplify round), 3d7696fd (code-review tail), 93a34711 (park-gate
fix), plus this closure commit.
<!-- SECTION:NOTES:END -->
