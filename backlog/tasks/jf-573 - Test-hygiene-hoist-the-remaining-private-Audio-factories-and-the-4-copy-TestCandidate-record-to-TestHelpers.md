---
id: JF-573
title: >-
  Test hygiene: hoist the remaining private Audio factories and the 4-copy
  TestCandidate record to TestHelpers
status: To Do
assignee: []
created_date: '2026-09-15 22:58'
labels: []
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill.Tests/Unit/TestHelpers.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Unit/PostPlayHandlerTests.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Unit/FindSongIntentHandlerTests.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Unit/SearchServiceTests.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
JF-315 test-hygiene follow-up from the batch-6 /simplify reuse review. TestHelpers.CreateSong (Jellyfin.Plugin.AlexaSkill.Tests/Unit/TestHelpers.cs ~line 126) is documented as "The ONE Audio factory" (the batch-4 reuse pass hoisted the third private copy there), yet two stale reflection-based copies survive and one new copy was almost added:

1. Jellyfin.Plugin.AlexaSkill.Tests/Unit/PostPlayHandlerTests.cs ~line 493: private CreateAudioItem via typeof(BaseItem).GetProperty("Id"/"Name")!.SetValue. Replace call sites with TestHelpers.CreateSong(name, id) and delete the helper.
2. Jellyfin.Plugin.AlexaSkill.Tests/Unit/FindSongIntentHandlerTests.cs ~line 2025: the same reflection shape. Same treatment. (Verify first whether any call site asserts on the reflection-set Id; CreateSong sets Id via plain object initializer, which is equivalent.)

Additionally the suite carries a 4-copy pile of the private record TestCandidate(string Name, Guid Id): Handler/FuzzyMatchAutoAcceptTests.cs ~958, Handler/HandleFuzzyMissNullGuardTests.cs ~138, a class-form at Unit/FuzzyMatchConfigurationTests.cs ~407, and the new Unit/SearchServiceTests.cs. The established hoist home is TestHelpers (the CreateSong/FakeSongIndex precedent): add one internal shared record and migrate the four files.

Mechanical-only migration: assertions must not change, only the construction/import sites; suite green both TFMs afterward. Related sibling: JF-571 (the CreateJellyfinUser dedicated batch), same hygiene series.
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
