---
id: JF-896
title: >-
  Entity-fallback polish: non-exact-rival subclass still lands clean-miss;
  exact-first idiom at two sites wants consolidation on next touch
status: Done
assignee: []
created_date: '2026-10-16 10:02'
updated_date: '2026-10-16 12:24'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-892 round (worker handback, 2026-10-16), two related polish items on the same fallback, one task: (1) BEHAVIORAL GAP: the JF-892 exact-first reorder rescues only the EXACT subclass; a coincidental-containment winner shadowing a better NON-exact rival (raw 92 / fair ~80 vs winner fair 18 in the fixture) still lands the clean not-found. The JF-776 refuse-and-continue walk is the follow-up shape; comment-documented and pinned green deliberately on the reshaped JF-894 pin (non-exact rival). (2) IDIOM CONSOLIDATION: the exact-first idiom now exists at two sites with different tails (PlayAlbumIntentHandler JF-743, CrossMediaFallback JF-892); consolidate on the next touch of either. Same class of note: only the TryEntityFallbackAsync call site reorders; the other ScoreBestWithCodes callers keep the raw order, so a future caller can reintroduce the shadow (the in-memory tier-1 corner is incidentally fixed by the shared acceptance point today).
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
EXECUTED on branch jf896 (7d80b74f), merged to main ad65f139, pushed, DEPLOYED to minix (net10.0 Release into AlexaSkill_0.12.1.0, active-DLL md5-verified), config survived, probes green ('jones' ask byte-identical, play pipeline healthy). ITEM 1: the JF-382 coincidental-containment downgrade now consults the shared qualified-rival scanner (extracted verbatim from the JF-894 tie arbitration, one walk two consumers, coincidental-shaped rivals refused) before landing; a non-exact rival clearing AlternativeFullNameThreshold upgrades the clean not-found to the JF-420.2 ask naming both (rivals headline fair-descending, the demoted winner enters at its FAIR score per the code-review fix, !kana-gated like its JF-894 sibling, not gated on JF-363 modes); no qualified rival keeps the JF-382 landing byte-identical (pinned). Red proof both TFMs. The review round caught and fixed a real pre-merge bug: the winner would have headlined the ask at its raw containment floor; the pin asserts the rival precedes the winner in speech and cycling state. ITEM 2 (consolidation): SKIPPED with the recorded reason (a shared helper needs a name selector plus secondary key across two divergent tails; parameterizing distorts both call sites). Residual noted in the record: an exact-named rival present ONLY in the rival pool keeps the downgrade landing (deliberately outside the task's non-exact rival definition). The old JF894_CoincidentalWinner_KeepsJf382Demotion_TieGateStandsDown pin was replaced (it pinned the now-fixed residual as green); its mechanism note lives in the new pin. Gates (in 7d80b74f): 0-warning build both TFMs, 6034/6034 both TFMs, /simplify 4 agents (shared AskRankedArtists builder et al.), code-review high 5 findings dispositioned. gate-exempt: gates ran in the dispatched worker's transcript; commit 7d80b74f carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->
