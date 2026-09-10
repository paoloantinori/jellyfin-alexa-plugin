---
id: JF-535
title: >-
  Hygiene one-liners: TryTransitionToReady whitespace token gate + undisposed
  class-lifetime DeviceQueueManagers in ~10 test fixtures
status: To Do
assignee: []
created_date: '2026-09-10 06:46'
labels:
  - tech-debt
  - tests
  - hygiene
dependencies: []
references:
  - JF-528
  - JF-453
  - JF-449
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two pre-existing hygiene one-liners surfaced by the JF-528 /simplify altitude pass (2026-09-10), both outside that diff's scope:

1. Entities/User.cs:163 - User.TryTransitionToReady gates "linking completed" on string.IsNullOrEmpty(JellyfinToken). Same discriminator class as the JF-528 fix in BaseHandler.BuildSessionMissResponse: a whitespace-only token should also fail the gate. Swap to IsNullOrWhiteSpace for uniformity (config-persistence path; verify the linking flow tests stay green).

2. Class-lifetime DeviceQueueManager instances never disposed in ~10 test fixtures (EventHandlerTests.cs:48, PlayAlbumIntentHandlerTests.cs:40, PlayBookIntentHandlerTests.cs:37, LastPlayedResponseInterceptorTests.cs:40, FollowMeIntentHandlerTests.cs:56, ResumeConfirmationTranscodeBaseTests.cs:56, PauseResumeStateTests.cs:45, PlayBookResumeTests.cs:43, ResumeIntentAudioVariantOffsetTests.cs:50, AplUserEventHandlerTests.cs:53/811): their armed 2s debounce timers fire post-test; because these point at Path.GetTempPath() rather than a registered sweep dir, the late writes drop stray queue_*.json files in the shared temp root (lower severity than the JF-453 registered-dir class, same hygiene family). Fix shape: make the fixtures IDisposable (or dispose in an existing teardown) mirroring GaplessPlaybackTests.cs:546/PauseResumeStateTests.cs:484's using-var convention. Related memory: jf453_temp_dir_sweep.md tracks the sibling leak class.
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
