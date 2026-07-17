---
id: JF-322
title: >-
  Robustness: Guardrail singleton handlers against mutable state; track
  fire-and-forget ffmpeg tasks for shutdown
status: To Do
assignee: []
created_date: '2026-07-12 14:59'
updated_date: '2026-07-13 20:18'
labels:
  - concurrency
  - reliability
milestone: m-8
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/EntryPoints/Registrator.cs:122'
  - 'Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs:262'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two robustness items from the 2026-07-12 architecture review (both currently safe, both fragile):

1. All 61 handlers are registered as singletons (`EntryPoints/Registrator.cs:122`, `AddSingleton(typeof(BaseHandler), handlerType)`). Verified safe today — handlers hold only injected deps, no per-request mutable fields. But there is zero guardrail: the day someone adds a mutable instance field (a natural thing to do), it becomes a silent cross-request data race under concurrent Alexa traffic. Fix: document the stateless constraint prominently (a class-level comment/analyzer), or make handlers transient/scoped so per-request state is safe by construction. Pair with marking injected fields readonly (JF-317).

2. Fire-and-forget ffmpeg monitors and background tasks (`VideoAudioController.cs:262,398,656` `_ = MonitorFfmpegHlsAsync(...)`; `Task.Run` at ~1444,1447,1564) are detached from any request/host lifetime; a hung ffmpeg is bounded only by an internal timeout CTS. Fix: register these with the hosted-service lifetime so they get graceful shutdown cancellation and central tracking.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The stateless-handler constraint is enforced or made structurally safe (documented + guarded, or handlers made transient/scoped)
- [ ] #2 Background ffmpeg monitor/remux/eviction tasks are tracked and cancelled on host shutdown rather than fully detached
- [ ] #3 A hung ffmpeg process is bounded and cleaned up on shutdown
- [ ] #4 No regression to streaming/encode behavior; test suite passes
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
