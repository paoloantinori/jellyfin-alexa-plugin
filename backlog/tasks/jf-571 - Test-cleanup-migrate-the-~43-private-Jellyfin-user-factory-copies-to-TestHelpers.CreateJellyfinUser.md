---
id: JF-571
title: >-
  Test cleanup: migrate the ~43 private Jellyfin-user factory copies to
  TestHelpers.CreateJellyfinUser()
status: In Progress
assignee: []
created_date: '2026-09-15 13:55'
updated_date: '2026-09-17 08:26'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Migration complete (2026-09-17, mechanical batch). All 77 raw `new Jellyfin.Database.Implementations.Entities.User(...)` occurrences across 36 files migrated to TestHelpers.CreateJellyfinUser(); grep for the raw ctor outside TestHelpers.cs returns 0 hits. TestHelpers.CreateJellyfinUser gained optional params (name, authProviderId, passwordProviderId) to absorb the 8 non-default sites (7x ("Paolo","auth","provider") + 1x ("CompletelyDifferentName","auth","provider") in PlayFavoritesIntentHandlerTests) and the parameterized SetupJellyfinUser helper in UserSkillApiTests. Two sites needed the Id set after construction (User.Id set via initializer before; object-initializer syntax is invalid on a method result): UserSkillApiTests.SetupJellyfinUser and PlayNextEpisodeIntentHandlerTests line ~252. One inline literal inside TestHelpers itself also migrated. No documented stragglers remain. Suite green both TFMs (4028/4028 each).
<!-- SECTION:NOTES:END -->
