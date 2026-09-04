---
id: JF-491
title: Consolidate the effective fuzzy bar idiom into a named helper
status: Done
assignee: []
created_date: '2026-09-04 20:07'
updated_date: '2026-09-04 21:29'
labels:
  - cleanup
  - fuzzy-matching
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The composition Math.Max(FuzzyMatcher.GetDefaultThreshold(user), bar) now appears as a repeated unnamed idiom across four sites: HandleFuzzyMiss auto-accept bar + no-qualifier bar (BaseHandler.cs ~2161/~2179, where the effective value is exactly this Max), FindSongIntentHandler singleCandidateAutoPlayThreshold (JF-487), and the CrossMediaArtistThreshold/CrossMediaAlbumThreshold doc comments (BaseHandler.cs ~83/~105) that prescribe the same idiom in prose. A FuzzyMatcher.GetEffectiveThreshold(user, bar) one-liner plus call-site/doc-comment updates would give the concept one name. Deferred from the 2026-09-04 /simplify pass on JF-487/488/489/490 (REUSE-1, low priority: one live call site, doc comments carry the rest, and the reviewer judged it naming value only). Do NOT bundle with a behavior change; pure consolidation.
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
Implemented 2026-09-04. Pure consolidation, zero behavior change. Per-site decisions:

- **Helper**: added `FuzzyMatcher.GetEffectiveThreshold(Entities.User? user, int bar)` = `Math.Max(GetDefaultThreshold(user), bar)` in `Alexa/FuzzyMatcher.cs`, placed after `GetSuggestionThreshold`. Doc comment names the concept: the judgment layer's floor raised by the user's personal fuzzy threshold; recall-side scoring stays on the plain thresholds.
- **FindSongIntentHandler `singleCandidateAutoPlayThreshold` (JF-487 site): HELPER SWAPPED.** The old RHS was literally `Math.Max(GetDefaultThreshold(user), ContainmentScore)`; the swap is provably identical (same two ints through the same int Math.Max, result still implicitly widens to the double local). The JF-487 comment now names the helper.
- **HandleFuzzyMiss auto-accept bar: CODE LEFT, comment names the helper.** The check is `score >= GetDefaultThreshold(user) || (behavior == AutoPlay && autoPlayFunc != null)`. This is an OR with a behavior clause, not `score >= Max(t, bar)`: there is no second bar in the comparison, so the helper is not even expressible here. The comment now states the two-step shape and why the plain threshold is kept.
- **HandleFuzzyMiss no-qualifier bar: CODE LEFT, comment names the helper.** Swapping `score >= ContainmentScore` for `score >= GetEffectiveThreshold(user, ContainmentScore)` is NOT semantically identical: in AutoPlay mode with a raised user threshold (say 95), a score of 92 currently plays WITHOUT the qualifier (auto-accept came from the behavior clause, bypassing the threshold), while the helper would attach the qualifier (92 < Max(95, 90)). The comment records the bare-bar rationale and the Confirm-mode composite, where the two steps DO compose into exactly the effective bar (the composite the JF-487 site applies).
- **Doc comments**: `CrossMediaArtistThreshold` and `CrossMediaAlbumThreshold` summaries now prescribe `FuzzyMatcher.GetEffectiveThreshold(user, <constant>)` instead of the inline `Math.Max(...)` prose.
- **Test**: `FuzzyMatcher_GetEffectiveThreshold_RaisesFloorByUserThreshold` added to `Tests/Unit/FuzzyMatchConfigurationTests.cs` (null user, default-threshold user, raised-threshold user).

OUT-OF-SCOPE DISCOVERY (left untouched, for the orchestrator): the idiom also appears LIVE at two sites the task's list did not name: `TryEntityFallbackAsync` (BaseHandler.cs ~line 3403, `Math.Max(normalThreshold, CrossMediaArtistThreshold)`) and `TryAlbumFallbackAsync` (BaseHandler.cs ~line 3762, `Math.Max(FuzzyMatcher.GetDefaultThreshold(user), CrossMediaAlbumThreshold)`), plus prose mentions in the comments around them (~3387, ~3672). Both sit in the cross-media fallback territory carrying uncommitted JF-442/458 work from another session, so they were not touched. Both are provably identical swaps if a follow-up wants to finish the consolidation.

Verification (worker session, 2026-09-04): `dotnet build Jellyfin.Plugin.AlexaSkill.sln` 0 errors 0 warnings; `dotnet test Jellyfin.Plugin.AlexaSkill.Tests` 3243 passed / 0 failed (baseline 3242 + the new helper test). Gates ran in the worker session: /simplify (one comment-redundancy fix applied: the HandleFuzzyMiss auto-accept comment no longer duplicates the no-qualifier comment's AutoPlay rationale) and the review-local DoD gate run directly in-context (sub-agents forbidden for this worker); its single finding >= 80 is the doc/application spelling mismatch above, already tracked here.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented and deployed 2026-09-04 (commit 24b77dc7). FuzzyMatcher.GetEffectiveThreshold(user, bar) names the effective-bar idiom; applied at the FindSong single-candidate bar and both cross-media fallback thresholds (TryEntityFallbackAsync keeps the bare normalThreshold local for the JF-363 suggestion band). HandleFuzzyMiss bars deliberately stay as code with comments naming the idiom: the auto-accept check is an OR with a behavior clause, and the no-qualifier bar must stay bare because the AutoPlay disjunct admits sub-threshold scores (swapping would attach the qualifier to sub-bar AutoPlay winners, a behavior change). The worker's review pass surfaced two additional live sites (TryEntityFallbackAsync, TryAlbumFallbackAsync) beyond the task's original list; the orchestrator applied both swaps (value-identical) to close the finding. Doc comments prescribe the helper. 1 new pin test; full suite 3243/3243.
<!-- SECTION:FINAL_SUMMARY:END -->
