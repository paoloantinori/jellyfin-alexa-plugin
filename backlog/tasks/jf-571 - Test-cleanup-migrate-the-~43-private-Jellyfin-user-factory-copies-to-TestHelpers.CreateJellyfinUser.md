---
id: JF-571
title: >-
  Test cleanup: migrate the ~43 private Jellyfin-user factory copies to
  TestHelpers.CreateJellyfinUser()
status: To Do
assignee: []
created_date: '2026-09-15 13:55'
labels:
  - tech-debt
  - test-cleanup
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill.Tests/Unit/TestHelpers.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The JF-315 batch 2 /simplify review added TestHelpers.CreateJellyfinUser() as the ONE shared factory for the Jellyfin server-side test user, but ~43 test files still carry private copies of `new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test")` (77 raw occurrences across 44 files; e.g. MusicPrimaryPathGateTests.cs:311, CrossMediaFallbackMusicGateTests.cs:186, GetRecentlyPlayedItemsTests.cs:66, ContentAccessEmptyKindsGateTests.cs). Same shape as the JF-465 HandlerTestFixture consolidation. Mechanical migration only: replace each private factory / inline literal with a call to TestHelpers.CreateJellyfinUser(); no expectation changes; full suite green both TFMs. Verify with: grep -rn 'new Jellyfin.Database.Implementations.Entities.User("testuser"' Jellyfin.Plugin.AlexaSkill.Tests --include=*.cs (target: 0 hits outside TestHelpers.cs).
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
