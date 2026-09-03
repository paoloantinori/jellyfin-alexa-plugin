---
id: JF-465
title: >-
  Consolidate duplicated handler test fixtures and the 10-copy warming-gate
  preamble
status: Done
assignee: []
created_date: '2026-09-03 08:20'
updated_date: '2026-09-03 11:59'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented, deployed, and live-smoke-verified (commit 9efcf434). Consolidation only, zero behavior change, verified by a whole-population normalization diff of all 32 converted test files (zero changed asserts, byte-identical stubs, handler ctor argument order unchanged) performed by the review pass.

1. HandlerTestFixture hoisted into TestHelpers (FakeArtistIndex/SharedGateProbeHandler precedent): the six-member mock set 33 handler test files carried line-for-line. 32 of 33 converted, net minus 745 test lines; EventHandlerTests excluded with reason (no server address + ctor DeviceQueueManager wiring); genuinely-semantic local variants (DeviceId/PlayState sessions, parameterized contexts, CreateUserJellyfin setups) stay local by design.

2. BaseHandler.GuardIndexReady (two index overloads) owns the JF-419 Layer-1 rationale once; all 13 EnsureReady call sites across 10 handlers replaced (actual count 13, not the task's estimated 11); per-site ordering notes kept only where a real constraint exists; Layer-2 sites keep direct EnsureReady deliberately.

3. WarmingGateCoverageTests: plain-reflection IL scan (no Cecil dependency) asserting set equality between the expected roster and every BaseHandler subclass whose IL calls a gate, including nested lambda display classes and constructors. CLAUDE.md Layer-1 and the SkillWarmingUpTests header point at this roster as the source of truth (both hand-maintained lists had gone stale once before). Review hardening applied from the gate pass: the token set is derived by enumerating every GuardIndexReady/EnsureReady overload BY NAME (open-world over the gate surface; the hardcoded two-plus-two set could let a future overload silently escape) and the scan walks base-type chains so a gate in an intermediate base class attributes to its concrete handlers; the P3@85 handoff-narrative gap (SkillWarmingUpTests header still naming the pre-refactor EnsureReady call shape) fixed with a pointer to GuardIndexReady and the roster.

Verified: suite 3076/3076 (baseline 3075 + the roster test; 466 [Fact]/[Theory] in changed files before and after, none dropped), Release 0 warnings, validator 90-warning baseline, both-direction roster mutation checks, IL-scan cost 17ms. Live smoke on minix after the DLL swap: PlayArtistSongs pink floyd plays through the consolidated gate wiring, config survived (1 user), no FTL/ERR in logs.

Gates: /simplify + code-review combined in one pr-review-toolkit:code-reviewer pass (behavior-preservation verified stronger than the requested sample: full-population normalized diff; 2 P3s found at 85 and 80, both applied same-turn; verification artifacts /tmp/normcheck.out + /tmp/normcheck.py preserved).
<!-- SECTION:FINAL_SUMMARY:END -->
