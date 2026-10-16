---
id: JF-891
title: >-
  JF-894: 'musica si nora jones' played Danko Jones - mood-fallback pick
  arbitrary among tied containment matches
status: Done
assignee: []
created_date: '2026-10-16 05:30'
updated_date: '2026-10-16 07:28'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Paolo's 2026-10-16 07:21 device round (corr 72eca45c): 'chiedi a mia collezione musica si nora jones' (typo: 'si' for 'di') auto-played DANKO Jones instead of Norah Jones. Full evidence chain from the logs: (1) the NLU routed PlayMoodMusicIntent with mood='jones' (the words 'si nora' were dropped by slot filling - bare 'musica si nora jones' reproduces mood='jones' on profile-nlu; the full wrapper reads Fallback on the profiler but routed on-device); (2) mood='jones' resolved genres=[jones] (garbage), no tracks; (3) the artist-genre fallback found 0; (4) the artist fallback with query='jones' tier-1 InMemoryContains returned 5 artists (the library carries TEN Jones artists: Norah, Tom, Quincy, Danko, Sharon Jones & the Dap-Kings, Keziah, Little Johnny Jones, Brian Jonestown Massacre, etc.); (5) 'PlayMoodMusic artist fallback: artist fallback found Danko Jones with score=90 (threshold=85)' -> auto-played. ROOT (two layers): (A) plugin-side, the real bug: the PlayMoodMusic artist fallback (the TryEntityFallbackAsync/JF-363 family) does NOT apply the JF-420 containment-vs-alternatives discipline nor the JF-885 dominance logic - a single-word containment match ('jones' inside 'Danko Jones', score 90 = the containment band) auto-plays when MULTIPLE artists tie at the same containment score (Norah Jones would score 90 too; the pick is arbitrary). PlayArtistSongs solves exactly this with FairComparisonScore margins + the yes/no prompt. FIX: route the mood-fallback artist selection through the same discipline (score ALL containment-band candidates; margin <= 20 with >= 2 distinct artists -> the JF-420.2 disambiguation prompt, never an arbitrary auto-pick); audit the OTHER TryEntityFallbackAsync call sites for the same hole. (B) NLU-side (context, not fixable by us): 'si nora' dropped by slot filling degraded 'nora jones' to 'jones'; the one-shot wrapper reads Fallback on profile-nlu but routed on-device (the wrapper-tail normalization again). GUARDS: single-word query matching >= 2 containment-band artists -> ask (Danko-vs-Norah both at 90); a UNIQUE containment match at >= 85 still auto-plays (existing behavior); Norah Jones by full name still plays directly; the existing PlayMoodMusic fallback pins stay green.
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
EXECUTED on branch jf894 (119ecca6), merged to main 371f77d9, pushed, DEPLOYED to minix and build-verified (ResolveContainmentTie x6 on the active DLL, health 200, config 1 user). The fix at the ONE choke point: CrossMediaFallback.ResolveContainmentTie wired into TryEntityFallbackAsync after the JF-382 downgrade - every pool rival scored through the shared phonetic scorer, fair-scaled symmetrically, dominant winner auto-selects, unique/exact plays unchanged, any qualifying rival within the JF-420 margin fires the JF-420.2 yes/no ask. All five TryEntityFallbackAsync call sites covered by construction (PlaySong second-site pin proves it). The JF-420 comparison core extracted to the new ArtistFairComparison.cs (one home; PlayArtistSongs byte-identical). 10 new pins incl. THE regression pin ('jones' over Norah+Danko asks, both listed); RED-PROOF stash-revert on both TFMs. Gates: /pa:reflect ALIGNED + /simplify (5 applied, 4 skips) + /code-review high (4 applied, 1 decided: Off-mode still asks, scope-noted). Tests 6010/6010 BOTH TFMs; Release 0/0. Follow-up draft filed by the worker (the exact-artist-shadowed-by-coincidental-candidate JF-776 B1 class, pre-existing). DEVICE RETEST: 'musica si nora jones' now asks 'Ho trovato N artisti: Norah Jones, Danko Jones...' with yes/no cycling; a unique match still auto-plays; 'norah jones' full name plays directly.
<!-- SECTION:FINAL_SUMMARY:END -->
