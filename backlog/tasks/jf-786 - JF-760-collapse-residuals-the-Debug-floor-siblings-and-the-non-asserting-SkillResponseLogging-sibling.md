---
id: JF-786
title: >-
  JF-760 collapse residuals: the 5 Debug-floor sibling blocks (accept vs
  collapse decision) and the non-asserting SkillResponseLogging capture site
status: To Do
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
- [ ] #1 Residual 1 decision recorded (accept vs collapse) with the reasoning
- [ ] #2 If collapse: arrange-only, zero Assert changes, non-vacuity red-check, full suite green both TFMs
- [ ] #3 Residual 2: SkillResponseContent_LogsAtDebugLevel_NotInformation actually asserts the Debug-not-Information contract (pipeline driven, logRecords asserted) or the test is renamed/removed with the intent re-homed
- [ ] #4 Full suite green both TFMs after any change
<!-- DOD:END -->
