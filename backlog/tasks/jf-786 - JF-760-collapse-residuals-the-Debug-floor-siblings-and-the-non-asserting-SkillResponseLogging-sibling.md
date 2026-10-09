---
id: JF-786
title: >-
  JF-760 collapse residuals: the 5 Debug-floor sibling blocks (accept vs
  collapse decision) and the non-asserting SkillResponseLogging capture site
status: Done
assignee: []
created_date: '2026-10-06'
labels:
  - test-hygiene
dependencies:
  - JF-760
references:
  - >-
    backlog/tasks/jf-760 - Capture-only-Trace-logger-factory-idiom-~21-sites-a-SkillResponseLoggingTests-sibling-the-JF-751-follow-up-helper.md
  - >-
    backlog/tasks/jf-751 - family-agnostic-deleting-logger-factory-micro-helper-collapse-the-15-site-file-wide-idiom.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-06 same-turn from JF-760's review gates, two residuals of the collapse that the arrange-only mandate (zero Assert changes) rightly excluded from the JF-760 commit itself.

RESIDUAL 1 (from the /simplify altitude angle, independently corroborated by the reuse angle's census; both judged the shipped boundary correct and surfaced this for a decision rather than as a defect): JF-760 collapsed the 25 sites of the capture-only TRACE-floor factory idiom into `TestCaptureLogger.CreateCaptureLoggerFactory`, but 5 inline blocks with the IDENTICAL statement shape and a DIFFERENT floor survive:

    Jellyfin.Plugin.AlexaSkill.Tests/Handler/EventHandlerTests.cs      ~939, ~1024, ~1134, ~1232
    Jellyfin.Plugin.AlexaSkill.Tests/Handler/ProgressiveQueueTests.cs  ~483

each a `LoggerFactory.Create` block carrying `SetMinimumLevel(LogLevel.Debug)` + `AddProvider(TestCaptureLogger.Into(...))`.

Also correctly left, and NOT worth collapsing even if the decision below lands: the ~10 no-floor one-liners (`LoggerFactory.Create(b => b.AddProvider(TestCaptureLogger.Into(...)))` in the LibrarySyncService* fixtures and the inline constructor arguments in EventHandlerTests/ProgressiveQueueTests). The helper call is the same length as the inline expression, so there is no collapse to be had.

THE DECISION for residual 1 (decide-then-maybe-do, JF-759-style):
- **Accept the residual** (the leaning of both review angles): the Debug floor at those 5 sites is load-bearing the same way the Trace floor is (it is the contract those tests' asserts depend on), and folding them via an optional `LogLevel` parameter on the JF-760 helper is exactly the parameter-swamp shape JF-751 rejected when it kept the deleting and capture helpers separate. If accepted: record the acceptance in this task and change no code.
- **Collapse** (only if the family grows): if a THIRD factory-floor variant ever appears, that is the trigger to promote a floor-parameterized builder into TestCaptureLogger.cs and re-home all variants on it (the JF-760 altitude review's forward note). A collapse limited to these 5 sites buys 15 lines at the cost of parameterizing a constant the JF-760 doc calls load-bearing.

If collapsing anyway: arrange-only rules apply (zero Assert changes, non-vacuity by red-checking a Debug-asserting consumer under sabotage), and the JF-760 pin `TestCaptureLoggerTests` must stay green in its Trace-floor form (a parameterized builder must not weaken the pinned floor).

RESIDUAL 2 (from the /code-review high round, applied as a site comment in JF-760; the BEHAVIORAL fix needs this task): `SkillResponseLoggingTests.SkillResponseContent_LogsAtDebugLevel_NotInformation` is the one migrated site whose captured `logRecords` is never asserted; the test builds a `RequestPipeline` around the capture factory but never invokes `ExecuteAsync`, and its only asserts are counter-bucket checks. The test is therefore vacuous against its own name (the Debug-not-Information level contract is unverified), and it is the one site of the 25 that stays green under a capture-provider sabotage. The vacuity PREDATES JF-760 (the inline idiom had the same unused records); JF-760 only made it uniform and documented it at the site. The real fix (drive the pipeline with a logging handler and assert the captured records carry Debug, not Information) is an Assert change, out of JF-760's arrange-only mandate by design; it belongs here.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 Residual 1 decision recorded (accept vs collapse) with the reasoning (ACCEPTED with the per-site minimal-floor mapping, the zero-LogTrace census, the unfired third-variant trigger, and the JF-751 parameter-swamp rejection; the seed-helper site honestly corrected to floor-indifferent today)
- [x] #2 If collapse: arrange-only, zero Assert changes, non-vacuity red-check, full suite green both TFMs (N/A: accepted, no collapse performed, no residual-1 code changed)
- [x] #3 Residual 2: SkillResponseContent_LogsAtDebugLevel_NotInformation actually asserts the Debug-not-Information contract (pipeline driven, logRecords asserted) or the test is renamed/removed with the intent re-homed (the real controller path driven via HandleIntentRequest's empty-body route; Assert.Single plus Equal(Debug) on the captured record, reddening under both the level flip and the capture sabotage)
- [x] #4 Full suite green both TFMs after any change (5339/5339 net9.0 and net10.0)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

## Implementation record (2026-10-06)

RESIDUAL 1 DISPOSITION: ACCEPTED, no code change. The decision was made on per-site evidence, each block's asserts mapped to the production log statements they pin. EventHandlerTests ~939 (`PlaybackFinished_InterTrackGapWithQueuedSuccessor_KeepsSessionAlive`) asserts Contains on "index=2 of 5" plus "keeping the session alive", produced by `PlaybackFinishedEventHandler.cs:169` `Logger.LogDebug`; at the builder-default Information floor that assert fails, so the Debug floor is the minimal admitting floor. EventHandlerTests ~1024 (`PlaybackFinished_ExpiredSleepToken_...`) asserts "sleep timer expired", LogDebug at line 184; same dependence. EventHandlerTests ~1134 (`PlaybackFinished_ShuffleRandomAtLastPosition_...`) asserts "keeping the session alive" plus "shuffleRandom=True", the same LogDebug site. ProgressiveQueueTests ~483 (`PlaybackNearlyFinished_ThresholdGuardSkip_LogsGuardNameAndValues`) asserts "ContinuationFetch: skip, 4 items remain over threshold=2" plus "index=0 of 5", LogDebug at `PlaybackNearlyFinishedEventHandler.cs:403` (sibling guards at 365/375). So 4 of the 5 floors are assert-load-bearing exactly as the filing leaned; the honest correction is the fifth, the `SeedTwoTrackJf691Scene` helper at ~1232, whose consumers assert Information-level lines ("nothing was enqueued for this boundary" at line 198, DoesNotContain "queue exhausted" at line 204), so THAT floor is not assert-load-bearing today and stays in the Debug shape as the file family's consistent block form. Two further facts complete the decision. First, the plugin source contains zero `LogTrace` call sites (grep across production code), so a Trace-floor helper would capture identical records today; a collapse of these 5 sites would be behaviorally dormant but contractually wrong, each floor stating the capture window its site's asserts target, the exact accident-waiting boundary JF-760 held. Second, both collapse mechanisms were already rejected upstream: routing through the Trace-floor helper changes which records arrive (the filing's own boundary), and an optional LogLevel parameter on `CreateCaptureLoggerFactory` is the parameter-swamp shape JF-751 rejected when it kept the deleting and capture helpers separate, besides touching what the JF-760 pin guards. The filing's collapse trigger (a third explicit floor variant appearing) has not fired: the family is exactly two explicit floors (the helper's Trace, these 5 Debug blocks) plus the no-floor one-liners, whose census on this tree is 9 sites (LibrarySyncServiceSeriesTests.cs:49, LibrarySyncServiceEquivalenceClassTests.cs:75, LibrarySyncServiceLegIsolationTests.cs:61, EventHandlerTests 979/1634/1662, ProgressiveQueueTests 1708/1796/1941, all `LoggerFactory.Create(b => b.AddProvider(TestCaptureLogger.Into(...)))` on the default Information floor); migrating those is length-neutral per the filing, no collapse to be had.

RESIDUAL 2 DISPOSITION: the behavioral fix landed. `SkillResponseContent_LogsAtDebugLevel_NotInformation` now drives the REAL production site: it calls the one-owner `TestHelpers.EnsureRealPlugin()` (the code-review round's correction; the first draft hand-rolled `EnsurePluginInstance` and the wrapper's doc names this exact controller-test shape), constructs `AlexaSkillController` on the JF-760 capture factory (mocked IUserManager/ISessionManager, real RequestCounters, the constructor-required empty pipeline), sets a `DefaultHttpContext` with an explicit empty MemoryStream body, awaits `HandleIntentRequest()`, and asserts the named contract on the captured records: the served body is non-empty (the review round's addition; a logs-but-serves-empty regression would previously stay green), exactly one "Skill response:" record exists (Assert.Single) and its Level equals Debug, so no Information-level copy can exist (Single plus the level assert enforce both halves of the name; the simplify round removed a DoesNotContain that was strictly implied, with the implication documented at the site). The old body's counter assert is kept but now runs through the production path instead of a hand-called `RecordResponseSize` on a self-serialized response; the pipeline the old body built was dead arrange (never executed) and is now the controller's real constructor argument, annotated that this route returns before ExecuteAsync. The empty-body early return is the cheapest genuine route through the private `SkillResponseContent` (no signature, timestamp, session, or user setup); the filing's suggested "pipeline driven" mechanism would not even reach the site, since `RequestPipeline.ExecuteAsync` returns a SkillResponse and only the controller converts it via SkillResponseContent. The class joined `[Collection("Plugin")]` plus PluginTestBase per the repo rule for anything constructing Plugin.Instance; the assembly already disables test parallelization, so the 11 pure counter tests pay nothing. RED PROOFS, both executed on this tree and reverted: (A) flipping the production line `AlexaSkillController.cs:446` LogDebug to LogInformation reddens the test on both TFMs (Assert.Equal on the level; the pre-JF-786 body stayed green under exactly this mutation, the vacuity being fixed); (B) commenting out the helper's AddProvider reddens it (Assert.Single finds no match), the capture itself non-vacuous. The class is 12/12 green on both TFMs on the final state.

GATES: /simplify (4 parallel angles): reuse CLEAN with one nit skipped (EnsureRealPlugin vs the direct EnsurePluginInstance call, deltas immaterial at that round; the code-review round later surfaced the wrapper's one-owner doc contract and the nit was APPLIED there); simplification applied the DoesNotContain removal (strictly implied by Single plus the level assert) and skipped the MemoryStream-body-line removal (the explicit empty body documents the route's trigger instead of depending on DefaultHttpContext default-body trivia across framework versions); efficiency's two findings (double Snapshot, the subsumed DoesNotContain) both landed via that same removal, and its explicit checks cleared the PluginTestBase/collection overhead (nanosecond-scale, assembly already sequential) and confirmed EnsurePluginInstance is load-bearing for the controller ctor; altitude CLEAN at the right depth (the controller drive is strictly more direct than the DoD's pipeline sketch; reflection, interceptor-level tests, and production extraction all judged worse), its pipeline-plumbing comment applied, and it re-verified the 5 residual-1 blocks byte-identical and untouched. /code-review high (8-angle pass, executed the suite green itself): 2 findings, both applied (the EnsureRealPlugin one-owner wrapper with the two now-dead usings removed; the served-body non-empty assert). No out-of-scope finding was left unlanded and nothing needed the JF-787/JF-788 reserve. Forward note from two angles, recorded here per the hoist-on-the-third-copy convention: the AlexaSkillController construction in this test is the second site in the project (beside AccountLinkingXssTests); a third site should hoist a TestHelpers factory rather than copy again.

SUITES: 5339/5339 on BOTH TFMs on the final state (test count net-zero; the tree tip c50ef0a7 carries merges beyond JF-760's own 5320 count); the touched class filtered 12/12 both TFMs at every stage (initial rewrite, post-simplify, post-review); solution Release `--no-restore -warnaserror` 0 warnings 0 errors. TEST-ONLY: no production code, model, locale, or speech surface; no deploy.
<!-- SECTION:NOTES:END -->
