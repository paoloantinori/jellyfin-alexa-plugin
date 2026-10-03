---
id: JF-707
title: >-
  JF-707 - cap the multi-value ER artist ask's spoken list (long first-word
  families speak every member in one breath)
status: Done
assignee: []
created_date: '2026-10-02 11:05'
updated_date: '2026-10-03 12:03'
labels:
  - ux
  - disambiguation
dependencies:
  - JF-690
references:
  - >-
    backlog/tasks/jf-690 -
    JF-690-shared-first-word-catalog-synonyms-auto-play-Amazon's-top-ER-rank-no-disambiguation-prompt.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the orchestrator gate-marker review of the JF-690 merge
(commit 08a3322d, finding 3 of 5). The JF-690 gate feeds EVERY resolved candidate into
`DisambiguationHelper.AskMultipleArtists` uncapped, unlike `AskFirstMatch`'s Take(3).
JF-684's PartialNameSynonyms adds the bare first substantive word to EVERY artist
sharing it, so a library with several same-first-word artists (e.g. multiple "Earth*"
bands) can resolve 4-5 ER values at once; the ask then speaks the full name list in a
single utterance (the DisambiguateMultipleArtists string was designed for the JF-420.2
two-name shape) and the yes/no cycling walks all of them.

THE WORK is a UX-shape decision, then its pin: preferred form is SPEAK the top-N
(matching AskFirstMatch's N=3 convention) while the cycling state keeps the full
resolved list, so no artist becomes unreachable; the alternative (cap both speech and
cycling at N) must be weighed against silently making ranks 4+ unaskable. The change
lives in the gate's ask leg or a new AskMultipleArtists overload, NOT in the shared
JF-420.2 builder used by the containment gate (that path's list is bounded by the
fair-comparison ranking already). Pin: a 4-resolved-candidate leg speaks 3 names and
still cycles to the 4th.

AUDIT UPDATE (2026-10-02): the fix shape must cover the SECOND uncapped consumer - AskMultipleArtists is also called from CrossMediaFallback.cs:1001 (the JF-363 cross-media offer), which inherits the same uncapped spoken list.

DECISION (2026-10-03, worker): SPEAK the top MultipleArtistsSpeakCap = 3 (AskFirstMatch's
N) while the cycling state keeps the FULL resolved list. Weighed against the alternative
(capping both speech and cycling, AskFirstMatch's exact truncation shape): AskFirstMatch's
callers pass progressively weaker fuzzy candidates, where a truncated rank-4+ tail is
noise; the JF-690 ER gate's candidates are EXACT library-name resolutions (each one is an
artist the user's spoken word literally resolved to), so capping the cycling list would
make ranks 4+ silently unaskable: three "no"s and then "no more matches" for an artist
that exists and was explicitly resolved. Under the chosen shape the unspoken rank stays
reachable and is discovered at its own turn: DisambiguateNext speaks each next name, and
the DisambiguateMultipleArtists reprompt already teaches the cycling ("Vuoi il primo?
Di' no per il successivo").

IMPLEMENTATION SHAPE: the cap lives in AskMultipleArtists ITSELF (a named const,
unconditional; no signature change), not at call sites and not as a second overload.
(1) The audit's cover-BOTH-consumers-by-construction requirement is only satisfiable at
the builder: a per-site cap cannot cover CrossMediaFallback's call, and an overload pair
forks the builder into capped/uncapped twins the next caller can pick wrong. (2) Every
other consumer passes a fixed 2-element list today (the JF-420 containment pair, both
JF-652 kana near-tie pairs), so Take(3) is the identity for them: byte-identical output,
zero blast radius. This supersedes the task text's "not in the shared JF-420.2 builder"
line, whose motivation (protect the already-bounded containment path from a behavior
change) holds vacuously under the builder-level cap; the deliberate asymmetry with
AskFirstMatch (which truncates the STATE list too) is documented on the const.

AUDIT PREMISE CORRECTION (2026-10-03): the audit update's "second uncapped consumer at
CrossMediaFallback.cs:1001 (the JF-363 offer)" does not exist as described.
BuildCrossMediaArtistOfferAsk (the JF-363 offer) builds its single-candidate ask directly
and never calls AskMultipleArtists. The actual second AskMultipleArtists consumer in
CrossMediaFallback.cs is the JF-652 kana near-tie wrapper (ResolveKanaOriginTie, line
~990 in this tree), a fixed pair; it is covered by the builder-level cap as a no-op,
which still satisfies the cover-both requirement.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (dotnet build Jellyfin.Plugin.AlexaSkill.sln: Build succeeded, 0 Errors, both TFMs)
- [x] #2 dotnet test passes (FINAL state: 5015/5015 net9.0 AND net10.0, exit 0, dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1, the full suite run ONCE on the final state per the workflow discipline; predicted 5013 baseline + 2 net new (the builder pin + the handler pin; the below-cap control was deleted in the simplify round as redundant with the both-in-library legs), prediction matched. Filtered runs: the two touched classes 70/70 both TFMs pre-simplify; the touched + neighbor classes (adoption, NoIntent) 100/100 both TFMs post-gates. REWORK ROUND, gate-marker F1/F2 (2026-10-03): +1 pin -> 5016/5016 net9.0 AND net10.0, exit 0, full suite run once on the final rework state; filtered touched suites 101/101 both TFMs (disambiguation + adoption + NoIntent neighbors) and 130/130 incl. the YesIntent family)
- [x] #3 No new compiler warnings introduced (the only build warning is the pre-existing xUnit1030 pair at VideoAudioControllerTests.cs:1499, a file this diff does not touch; identical before and after)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (unchanged: the ask still stores List<MatchInfo> via BuildAttributes)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (handler-level pins instead of SMAPI e2e: PlayArtistSongs_MultiValueEr_FourInLibrary_SpeaksThree_CyclesToTheFourth drives the real gate and the real NoIntentHandler through three advances to the unspoken 4th name, then the NoMoreMatches exhaustion leg; AskMultipleArtists_FiveMatches_SpeaksFirstThree_StoresAllFive pins the builder. Red proof run on both TFMs: cap removed -> both pins fail, the 4th name spoken in the initial breath. REWORK ROUND, gate-marker F1: PlayArtistSongs_MultiValueEr_FourInLibrary_YesAtUnspokenRank_PlaysIt pins the READ side (yes at cycled index 3 beyond the cap resolves by id and PLAYS the unspoken 4th artist, scoped-query + directive-token + queue asserted, the PlayAlbumIntentHandler yes-pin wiring); its red proof is the exact future-harmonization sabotage, a Take(3) at YesIntentHandler's stored-list consumer -> ONLY this pin fails, both TFMs, no fairies-scoped play recorded)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new user-facing strings; the existing DisambiguateMultipleArtists is reused unchanged, and the code-review gap this leaves (the capped list still reads as an exhaustive found-set) is FILED as JF-729)
- [x] #9 /simplify passed (4 agents: efficiency clean; reuse 1 APPLIED (the three hand-rolled AttrMatches parses swapped for DisambiguationHelper.ReadState); simplification: rationale de-duplication APPLIED (full cap rationale homed on the const doc), the below-cap TwoMatches control DELETED (redundant with the both-in-library legs, which pin the 2-name shape end-to-end through the real gate), the stored-state half of AssertMultiArtistAsk extracted APPLIED, and 1 skipped (the optional TestHelpers hoist of the NoIntent drive: a single multi-turn walk, matches the per-suite convention); altitude 0 code findings, verdict RIGHT at the builder, its optional AskFirstMatch-asymmetry note APPLIED to the const doc. REWORK ROUND, gate F2 refresh: 4 agents again; reuse clean + the IntentNames.AmazonYes/AmazonNo constants APPLIED to the new request builders; simplification: the AssertNoDisambiguationState twin drift (one-key vs two-key across the two suites, flagged by two agents) APPLIED via the TestHelpers two-key hoist, skipped with reasons the Theory/ask-builder consolidation (per-test repetition is the file convention; the terminal legs differ) and the recording-mock merge (near-identical but differently routed); efficiency clean (the second NoIntentHandler construction is the minimal shape, the walk helper cannot serve the exhaustion leg); altitude RIGHT on all axes, its stale doc clause on AssertStoredArtistMatches APPLIED)
- [x] #10 /code-review high passed (0 correctness bugs: all 4 production call sites traced, NoIntent/YesIntent consumption and the 17 locale strings checked; findings 2/3/4 APPLIED (AssertStoredArtistMatches hoisted to TestHelpers as the ONE oracle both MusicianMultiValueEr suites now call; both ask-helper doc comments corrected to the top-cap contract; the terminal NoMoreMatches exhaustion leg added to the 4-artist pin); finding 1 FILED as JF-729 (the 17 DisambiguateMultipleArtists strings present the capped list as an exhaustive found-set; an "and N more" hint needs its own 17-locale pass). REWORK ROUND refresh: 0 correctness bugs again, 3 low findings all APPLIED (the builder pin now asserts the stored IDS too, not just names; the const doc no longer claims a live Take(3) convention linkage that could rot, the constants stay deliberately unlinked; this task-file refresh is the third finding's fix)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle including a rework round: worker commits b467a594 + b4358d40, merged as bd669793. The multi-artist ask's spoken list capped at MultipleArtistsSpeakCap=3 inside the shared builder (identity for every existing 2-element consumer; the JF-690 ER gate's unbounded leg the target), the cycling state keeping the FULL list so unspoken ranks stay reachable and are named at their turn. The audit premise corrected during the work (the JF-363 offer never called AskMultipleArtists; the kana near-tie pair is the real second consumer, covered as a no-op). The rework landed the orchestrator review's two findings: the yes-at-unspoken-rank pin (red-proven on both TFMs against the exact future-harmonization sabotage - a Take at the stored-list consumer fails ONLY this pin - then fully reverted) and the one cap-aware TestHelpers.AssertMultiArtistAsk oracle replacing the byte-identical twins across both suites. JF-729 filed (the 17-locale exhaustive-set wording). Worker gates green on both rounds (both Skills refreshed on the rework diff: simplify incl. the AssertNoDisambiguationState hoist; code-review high 0 correctness, 3 low all applied); the scaled orchestrator review verified all five axes at source (four production call sites traced, the blast-radius identity confirmed, the cycling-reads-stored-list confirmed, the 17 strings checked for active lies). Suites: worker 5015 then 5016 after rework (discipline held, prediction matched), filtered 101/101 and 130/130, merged-tree 5017/5017 both TFMs exit 0 on both split legs. Production surface changed (DisambiguationHelper): deployed in the post-closure deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
