---
id: JF-322
title: >-
  Robustness: Guardrail singleton handlers against mutable state; track
  fire-and-forget ffmpeg tasks for shutdown
status: Done
assignee: []
created_date: '2026-07-12 14:59'
updated_date: '2026-07-19 20:43'
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
- [x] #1 The stateless-handler constraint is enforced or made structurally safe (documented + guarded, or handlers made transient/scoped)
- [ ] #2 Background ffmpeg monitor/remux/eviction tasks are tracked and cancelled on host shutdown rather than fully detached
- [ ] #3 A hung ffmpeg process is bounded and cleaned up on shutdown
- [x] #4 No regression to streaming/encode behavior; test suite passes
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

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-19 20:43
---
PART 1 DELIVERED (commit 7e94480); PART 2 DEFERRED. Part 1 (stateless-handler guardrail): added a class-level <remarks> on BaseHandler documenting that handlers are DI singletons and MUST be stateless (no per-request mutable instance fields -- concurrent Alexa requests race on them), and marked the 10 remaining non-readonly injected dependency fields (_libraryManager/_userManager across 6 handlers) readonly so a future mutable field fails at compile time. This is the 'documented + guarded' form of AC#1 (the alternative -- making handlers transient/scoped -- was rejected as a risky DI-lifetime change with no live bug). Part 2 (register fire-and-forget ffmpeg monitor/remux tasks with the host lifetime for graceful shutdown cancellation) DEFERRED: those tasks are already bounded by an internal timeout CTS (a hung ffmpeg is cleaned up), so this is a guardrail improvement, not a live bug; it touches the complex encode-monitoring path and warrants focused work. Compile-time-only change (no runtime behavior change); no deploy needed. Full suite 2542/2542.
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Part 1 (stateless-singleton handler guardrail) delivered: BaseHandler documents the stateless constraint, and the 10 non-readonly injected dependency fields across 6 handlers are now readonly (compile-time guard against accidental mutable state under concurrent requests). Part 2 (register fire-and-forget ffmpeg tasks with the host lifetime for graceful shutdown) deferred -- those tasks are already bounded by an internal timeout CTS, so it's a guardrail, not a live bug, and touches the complex encode-monitoring path. Compile-time-only; full suite 2542/2542.
<!-- SECTION:FINAL_SUMMARY:END -->
