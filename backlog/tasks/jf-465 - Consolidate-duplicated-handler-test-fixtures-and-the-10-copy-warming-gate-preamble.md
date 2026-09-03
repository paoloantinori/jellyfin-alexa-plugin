---
id: JF-465
title: >-
  Consolidate duplicated handler test fixtures and the 10-copy warming-gate
  preamble
status: In Progress
assignee: []
created_date: '2026-09-03 08:20'
updated_date: '2026-09-03 11:04'
labels: []
dependencies: []
references:
  - JF-463 simplify findings E3/A2
  - JF-382 (search-path duplication precedent)
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/PlayByGenreFallbackTests.cs
  - CLAUDE.md Cold-Start Warming Gates section
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-463 /simplify pass (2026-09-03, efficiency+altitude agents, findings E3 and A2):

1. Test-fixture duplication: PlayByGenreFallbackTests.cs:40-88 and PlayByGenreIntentHandlerTests.cs:28-89 are near line-for-line copies (same six mocks, same CreateHandler, same SetupUserMock/CreateContext/CreateSession/CreateUser builders; only the default locale and one setup helper differ). The next optional ctor-dep edit touches every copy. Consolidate into a shared builder in TestHelpers (the suite already has the hoist discipline documented at TestHelpers.cs:223-226).

2. Warming-gate preamble copies: the two-line IndexWarmingGate.EnsureReady-before-progressive-response preamble with its near-copied rationale comment now exists in 10+ handlers (PlayByGenre, PlayMoodMusic, SearchMedia, PlayArtistSongs, PlayAlbum, PlaySong x2, FindSong, AddToQueue, PlayNext, QueryArtistLibrary). Each copy must be kept in sync manually; the CLAUDE.md Layer-1 list and the SkillWarmingUpTests enumeration must both be updated per addition (both were stale when found). Consider a BaseHandler helper (e.g. GuardArtistIndexBeforeAnnouncement(index)) that owns the comment once, plus a reflection-driven test asserting every handler whose HandleAsync calls EnsureReady appears in the enumerations (or drop the manual enumerations in favor of the reflection list).

This is consolidation-only: no behavior change, no new gates. Follow the JF-382 precedent for tracked search-path duplication (do not reflexively extract; but here the copies are pure boilerplate with no per-site variance beyond the index argument).
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
