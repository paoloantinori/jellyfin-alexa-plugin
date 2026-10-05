---
id: JF-760
title: >-
  Capture-only Trace logger factory idiom (~21 sites + a
  SkillResponseLoggingTests sibling): the JF-751 follow-up helper
status: Done
assignee: []
created_date: '2026-10-04 19:30'
labels:
  - test-hygiene
dependencies: []
references:
  - >-
    backlog/tasks/jf-751 -
    family-agnostic-deleting-logger-factory-micro-helper-collapse-the-15-site-file-wide-idiom.md
  - >-
    backlog/tasks/jf-759 -
    EncoderPath-mock-wire-up-in-the-three-HLS-sibling-fixtures-is-inert-decide-delete-vs-keep-JF-751-second-idiom-sabotage-proven.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-04 same-turn from JF-751's /simplify altitude round (out-of-scope for JF-751's arrange-only mandate; the second of JF-751's two filed findings, JF-759 being the first).

THE SHAPE: the capture-only logger factory block

    using var loggerFactory = LoggerFactory.Create(b =>
    {
        b.SetMinimumLevel(LogLevel.Trace);
        b.AddProvider(TestCaptureLogger.Into(logRecords));
    });

appears ~21 times in Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs (census from the altitude review: lines 766, 807, 2106, 2155, 2212, 2275, 2371, 2458, 2537, 2618, 3033, 4335, 4402, 4460, 5693, 5830, 6203, 6351, 7755, 7803, 9123 at review time; re-verify against the current file before extracting) plus a sibling in Jellyfin.Plugin.AlexaSkill.Tests/Unit/SkillResponseLoggingTests.cs (~line 149).

RELATION TO JF-751: JF-751 shipped CreateDeletingLoggerFactory(trigger, playlistPath, params extraProviders) for the 15 VANISH-RACE sites (all carry a FileDeletingLoggerProvider; its extras param already accepts TestCaptureLogger.Into ahead of the deleting provider). The ~21 sites here have NO deleting provider, so forcing them through the deleting helper would parameter-swamp; a separate capture-factory micro-helper (e.g. CreateCaptureLoggerFactory(params ILoggerProvider[]) or a Into-only form) is the natural shape. Same family rules apply: arrange-only, pin names and assertion content unchanged, non-vacuity by shared sabotage (dropping the capture provider must red the log-asserting consumers), zero-assert-diff provable by grep.

CAUTION: the SkillResponseLoggingTests site sits in a DIFFERENT file; a helper private to VideoAudioControllerTests cannot serve it. Decide the helper's home (per-file private vs TestCaptureLogger itself) after the census; TestCaptureLogger.cs is the natural shared home if the cross-file site is included.
<!-- SECTION:DESCRIPTION:END -->

## Implementation record (2026-10-06)

THE SHIPPED SHAPE: `TestCaptureLogger.CreateCaptureLoggerFactory(records)` (an internal static on TestCaptureLogger.cs, beside `Into` and `Snapshot`): `LoggerFactory.Create` at the Trace minimum with `Into(records)` registered. Home decision per the filing's CAUTION: the idiom spans two files (24 sites in VideoAudioControllerTests.cs plus the SkillResponseLoggingTests.cs sibling), so a per-file private cannot serve it; TestCaptureLogger.cs is the shared home, and the helper composes the existing `Into` rather than duplicating the provider machinery. JF-751's `CreateDeletingLoggerFactory` stays file-private in VideoAudioControllerTests.cs: all its call sites are in that one file and it cannot delegate without a signature change; the family split is the deliberate two-task design.

CENSUS RE-VERIFIED (the filing's instruction): the filing's ~21 was taken at its review time; on this tree the idiom is 24 byte-identical blocks in VideoAudioControllerTests.cs (the migration's exact-string replace asserted count 24) plus the SkillResponseLoggingTests sibling at its line 148 shape (`var`, builder-named lambda, no `using`), 25 sites total, all migrated. Boundary held (the JF-785 record): the 5 Debug-floor blocks (EventHandlerTests, ProgressiveQueueTests) and the ~10 no-floor one-liners (LibrarySyncService*, plus inline constructor arguments) are DIFFERENT capture contracts; routing them through a Trace-floor helper would change which records arrive, a behavior change, not an arrange.

ARRANGE-ONLY PROOF: zero Assert lines and zero Fact/signature lines in the diff (grep over every +/- diff line exits clean); all 24 controller-file sites keep `using var`, the sibling keeps its `using`-less `var`; trigger strings, locals, and disposal shapes untouched; net minus 82 lines on the migration.

NON-VACUITY, WITH A DISCLOSURE: the classic JF-692/JF-751 proof (mutate the helper, watch consumers redden) was DENIED by the session's permission classifier, which read the temporary sabotage edit as malicious interference; the denial was respected, not worked around. The shipped proof has three legs. First, a durable pin, `TestCaptureLoggerTests.CreateCaptureLoggerFactory_CapturesDownToTraceLevel`: it logs at Trace, Debug, and Information through a helper-built factory and asserts all three records arrive, so BOTH sabotage modes redden permanently (dropping the AddProvider fails `Assert.Equal(3, records.Count)`; raising the floor above Trace fails the Trace/Debug Contains asserts; the code-review round independently verified both failure modes analytically). This replaces a one-off manual sabotage run with an always-on guard, a strictly stronger shape, at the cost of one new test. Second, the static data-flow census: `CaptureLoggerProvider` is constructed ONLY by `Into` inside TestCaptureLogger.cs, so the helper's AddProvider is the only writer into `records`; all 24 controller-file sites reference `logRecords` after the factory (2 direct `Assert.Contains(logRecords, ...)` plus 22 via `TestCaptureLogger.Snapshot` asserts), and an empty record list fails every one of them. Third, neutrality: the touched classes 299/299 green on both TFMs before the full suite. The census's one hole is the SkillResponseLogging sibling, whose records are never asserted (pre-existing vacuity, predating this task); it is documented at the site and its behavioral fix is filed as JF-785 residual 2.

GATES: /simplify (4 parallel angles): reuse CLEAN, efficiency CLEAN, simplification 1 applied (the pin test's doc no longer restates the helper's Trace-floor rationale; the pin's four asserts kept, the Count pin is not derivable from the Contains), altitude CLEAN with its boundary note FILED as JF-785. /code-review high: 3 findings, all landed (F1 the non-asserting sibling documented at the site plus the behavioral fix filed as JF-785 residual 2; F2 the near-twin cross-reference added to the helper doc, naming StructuredLoggingTests' private `CreateCapturingLoggerFactory` and the mechanism difference; F3 the prose-rule reword in the JF-785 filing). No out-of-scope finding was left unlanded; JF-785 is the single filing for both residuals.

SUITES: 5320/5320 on BOTH TFMs on the final state (the worktree-tip baseline 5319 plus the 1 pin); the touched classes filtered 299/299 net9.0 and 298/298 pre-pin net10.0 (the pin rides the full suite on net10.0); solution Release `--no-restore -warnaserror` 0 warnings 0 errors. TEST-ONLY: no production code, no model, no locale, no speech surface; no deploy.

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Release --no-restore -warnaserror, 0 warnings 0 errors, both TFMs)
- [x] #2 dotnet test passes (5320/5320 BOTH TFMs on the final state; baseline 5319 + the 1 non-vacuity pin)
- [x] #3 No new compiler warnings introduced (Release -warnaserror clean)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session attributes touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: test-hygiene refactor only; the non-vacuity pin added at the helper level)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new strings)
- [x] #9 /simplify passed (4 angles: 1 applied, 2 CLEAN, 1 boundary note filed as JF-785; recorded in the implementation record)
- [x] #10 /code-review high passed (3 findings, all landed: 2 applied in-code, 1 absorbed into the JF-785 filing; recorded in the implementation record)
<!-- DOD:END -->
