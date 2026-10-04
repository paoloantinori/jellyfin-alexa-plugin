---
id: JF-760
title: >-
  Capture-only Trace logger factory idiom (~21 sites + a
  SkillResponseLoggingTests sibling): the JF-751 follow-up helper
status: To Do
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
