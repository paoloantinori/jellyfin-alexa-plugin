---
id: JF-573
title: >-
  Test hygiene: hoist the remaining private Audio factories and the 4-copy
  TestCandidate record to TestHelpers
status: Done
assignee: []
created_date: '2026-09-15 22:58'
updated_date: '2026-09-17 10:53'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Migration complete (2026-09-17, mechanical batch). (1) Both stale reflection-based Audio factories deleted: PostPlayHandlerTests.CreateAudioItem (13 call sites) and FindSongIntentHandlerTests.CreateAudioItem (40 call sites) replaced with TestHelpers.CreateSong(name, id[, genres]); equivalence confirmed, CreateSong sets Name/Id via a plain initializer which is what the reflection SetValue did. (2) The 4-copy TestCandidate hoisted to one internal record TestCandidate(string Name, Guid Id) at namespace level in TestHelpers.cs (nested in the static class does not resolve unqualified at call sites, so it sits beside the class). Private copies deleted from FuzzyMatchAutoAcceptTests, HandleFuzzyMissNullGuardTests, SearchServiceTests, and FuzzyMatchConfigurationTests; the class-form's object-initializer sites were rewritten positionally (new("Abbey Road", Guid.NewGuid()) etc.), preserving the unique-Id semantics the old class default gave. Assertions untouched. Suite green both TFMs (4028/4028 each).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DONE (commit 3f59aa54, shared with JF-571). The ONE Audio factory claim now holds: both stale reflection-based CreateAudioItem factories deleted (PostPlayHandlerTests 13 call sites, FindSongIntentHandlerTests 40 call sites, all onto TestHelpers.CreateSong with identical Name/Id semantics - reflection SetValue invoked the same public setters a plain initializer uses); the 4-copy private TestCandidate record/class hoisted to ONE internal record at namespace level in the TestHelpers file (nested-in-static-class placement did not resolve unqualified at call sites), with the mutable class-form file (FuzzyMatchConfigurationTests) rewritten positionally preserving fresh-Guid-per-instance semantics and record value-equality verified inert (no set/dict/Distinct usage anywhere); the last four private CreateTestAudio copies (PlayFavorites, PlayRandom, PlayLastAdded, ListTruncation) removed onto CreateSong; and the suite's final reflection-based factory (CreateArtist in FindSongIntentHandlerTests) converted to a plain initializer (equivalence proven by ArtistIndexServiceTests' 20+ direct constructions). Mechanical only: assertions unchanged, no production file touched. Gates: /simplify consolidated 4-angle pass (in-diff leftovers applied: orphaned summary above TestableBaseHandler, stale TODO, dead usings, self-namespace usings; residuals folded into the same batch); code-review high via feature-dev:code-reviewer verified CreateSong genre/null equivalence clean at every migrated site (plugin reads item.Genres null-safe on these paths) and found no inequivalent migration. Suite 4028/4028 both TFMs, Release 0 warnings.
<!-- SECTION:FINAL_SUMMARY:END -->
