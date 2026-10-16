---
id: JF-892
title: >-
  Entity fallback: exact artist shadowed by a coincidental-containment candidate
  listed earlier (JF-776 B1 class at TryEntityFallbackAsync)
status: Done
assignee: []
created_date: '2026-10-16 08:18'
updated_date: '2026-10-16 10:08'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found during JF-894's code-review round (commit 119ecca6). SHAPE: on the DB path of CrossMediaFallback.TryEntityFallbackAsync, when the SearchTerm tier returns a coincidental-containment candidate BEFORE the exact-named artist (e.g. query 'goldbug stories' with rows [Bug, Goldbug Stories]), Util.ArtistSearch.ScoreBestWithCodes early-exits at the first ContainmentScore crossing on 'Bug' (90) and the exact 'Goldbug Stories' is never scored: the JF-776 B1 shadowing class, at this fallback's acceptance point. The JF-382 downgrade then demotes the coincidental winner, and on the no-offer callers (PlayMoodMusic, FindSong, PlayByGenre) the user gets the clean not-found even though the library holds the artist they named. NOT a regression from JF-894: pre-existing behavior; JF-894's containment-tie arbitration deliberately runs AFTER the JF-382 downgrade (the PlayArtistSongs family order, pinned by JF894_CoincidentalWinner_KeepsJf382Demotion_TieGateStandsDown), so it does not rescue this corner. Candidate fix directions, to be weighed when taken: prefer-token-prefix/exact-first ordering of the chain results before the single-best collapse (the JF-598/JF-776 levers), or let the JF-382 demotion consult the tie gate's ranked rivals for an exact-named survivor before demoting. Reproduce: unit shape in Jellyfin.Plugin.AlexaSkill.Tests/Handler/EntityFallbackTests.cs (the ordering pin's fixture, with the mock returning the exact artist's songs, shows the not-found).
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
EXECUTED on branch jf892 (1eddb605), merged to main afde1c90, pushed, DEPLOYED to minix (net10.0 Release into AlexaSkill_0.12.1.0, active-DLL md5-verified) and live-verified: the 'jones' containment-tie ask preserved byte-for-byte ('Ho trovato 10 artisti: ... Vuoi il primo?'), play pipeline green, config survived. THE FIX: direction (a), a pure stable exact-first reordering of the chain results at the TryEntityFallbackAsync acceptance point one line before the ScoreBestWithCodes collapse (OrderByDescending IsExactNameMatch), plus a branch-decision Information log when a reorder rescues a promoted row; ScoreBestWithCodes/FuzzyMatcher untouched so the shared early-exit semantics and all five callers are unchanged. The JF-894 pin's fixture reshaped to a NON-exact rival with every assertion byte-identical (the exact row moved to the new JF-892 red test; the pinned downgrade-outranks-tie-gate order unchanged, documented in the pin's comment). Red proof both TFMs (the new exact-shadow pin failed with the clean not-found pre-fix). Gates (in 1eddb605): 0-warning build both TFMs, 6012/6012 both TFMs run twice, /simplify 4 agents (applied + reasoned skips), /code-review high (1 applied log-line finding, 2 reasoned skips, 1 FILED = JF-895 the un-fired auto-select pin; residual filed = JF-896 the non-exact-rival subclass + idiom consolidation). NLU fixtures/model N/A (no model surface). gate-exempt: gates ran in the dispatched worker's transcript; commit 1eddb605 carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->
