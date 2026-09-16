---
id: JF-570
title: >-
  JF-315 follow-up: unify the twin SortByRating overloads now split across
  BaseHandler (3-tuple) and ResumeMath (4-tuple)
status: Done
assignee: []
created_date: '2026-09-15 13:17'
updated_date: '2026-09-16 05:18'
labels:
  - refactor
  - tech-debt
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/ResumeMath.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Efficiency-review finding from the JF-315 batch-2 diff (BaseHandler -> Alexa/Util/ResumeMath.cs verbatim extraction), filed same-turn so the observation does not leak:

The extraction left TWO structurally identical SortByRating implementations split across files:
- Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs line 296: private static SortByRating(List<(int Index, BaseItem Item, double? Rating)>) — still used by the favorites-first rating sort block at BaseHandler.cs lines 291-292.
- Jellyfin.Plugin.AlexaSkill/Alexa/Util/ResumeMath.cs (moved verbatim): private static SortByRating(List<(int Index, BaseItem Item, double? Rating, UserItemData? Data)>) — used only by ResumeMath.SortAndFindResumeIndex.

Both are the same OrderByDescending(Rating ?? double.MinValue).ThenBy(Index).Select(Item) chain, differing only in tuple arity. This duplication PRE-DATES the batch (they were two overloads in BaseHandler); the move just froze the near-duplicate across a file boundary. Zero runtime cost today (both pre-existing); this is a maintenance cleanup only: when a later JF-315 batch touches either site, unify on the 4-tuple shape (or a shared projection) and delete the 3-tuple copy. Do NOT do it inside a verbatim byte-identity batch.
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
CLOSED by JF-315 batch 10 (2026-09-16). The twin SortByRating overloads were unified exactly as this task directed: FavoritesAndRatingsFirst moved from BaseHandler to ResumeMath (its twin machinery: it is the ordering half of SortAndFindResumeIndex), now building the 4-tuple (Index, Item, Rating, UserItemData Data) it already held the user data for, and calling the ONE shared 4-tuple ResumeMath.SortByRating; the 3-tuple BaseHandler copy is deleted. The consolidation was provably identity: both overloads' chains were token-identical (OrderByDescending(Rating ?? double.MinValue).ThenBy(Index).Select(Item)), differing only in tuple arity, and the Data element is write-only (no reader anywhere; noted in JF-572 for an eventual drop). Characterization facts (4, green on the pre-move code) pin the observable behavior: no-rating passthrough returns the ORIGINAL list instance, single-item passthrough, favorites-first + rating-desc-within-groups, and stable index-order on rating ties. QueryArtistLibraryIntentHandler re-pointed to ResumeMath.FavoritesAndRatingsFirst. Gates: full suite 3981/3981 both TFMs, Release build 0 warnings, /simplify 4-agent pass, 5-reviewer review-local pass with no findings >=80.
<!-- SECTION:FINAL_SUMMARY:END -->
