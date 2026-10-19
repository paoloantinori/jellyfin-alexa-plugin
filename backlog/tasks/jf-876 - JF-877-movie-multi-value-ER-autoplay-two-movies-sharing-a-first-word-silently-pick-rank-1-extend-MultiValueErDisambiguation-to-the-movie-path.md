---
id: JF-876
title: >-
  JF-877: movie multi-value ER autoplay - two movies sharing a first word
  silently pick rank-1 (extend MultiValueErDisambiguation to the movie path)
status: Done
assignee: []
created_date: '2026-10-12 21:12'
updated_date: '2026-10-19 20:54'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Residual 1 from JF-874 (worker report, 2026-10-12 night): the movie path reads GetCanonicalValue (rank-1 only), and PartialNameSynonyms now widens to Movie, so two movies sharing a first substantive word are both ER-reachable ("Alien" vs "Alien: Romulus"): Amazon's rank-1 silently picks one and it auto-plays through the exact-name path. The artist path already solved this class: MultiValueErDisambiguation (JF-690/JF-702) reads every ER canonical and fires the yes/no cycling on >=2 distinct library items. Extend the movie leg the same way when the household grows same-first-word movies (the 12-title seed today has no collision, which is why this is deferred: no live case exists).
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
DISPATCH 2026-10-19 21:20 (orchestrator): the deferral premise is REFUTED by live evidence; task unblocked and dispatched. (1) Library census (91 movies, $JELLYFIN_URL, PartialNameSynonyms.Generate replicated faithfully incl. the stop-word union and FunctionWords): SIX collision tokens: 'gruffalò' (Il Gruffalò / Il Gruffalò e la sua piccolina), 'monsters' (Monsters & Co. / Monsters University), 'winnie' (2 Winnie the Pooh), 'avventure' (3 movies), 'bella' (2), 'alla' (2). All kids' titles; the household outgrew the 12-title seed. (2) LIVE ER confirmation (profile-nlu it-IT, skill 33dfacd5, 'chiedi a mia collezione di mettere il film gruffalò'): selectedIntent PlayMovieIntent, slot movie ER_SUCCESS_MATCH with TWO values, Amazon rank-1 = 'Il Gruffalò e la sua piccolina' (the SEQUEL), rank-2 = 'Il Gruffalò'. So the silent pick plays the wrong film TODAY on a form a child uses. Priority LOW → MEDIUM on the live case. Anchors for the worker: PlayVideoIntentHandler.cs lines ~116-140 (the ACCEPTED code-review F2 comment names this exact follow-up), MultiValueErDisambiguation.cs (the artist composite: JF-690 wiring, JF-702 residual ledger, JF-885 dominance rule), SlotValueHelper.GetCanonicalValues, DisambiguationHelper (state machine + MediaTypeVideo constant already exist), YesIntentHandler's video disambiguation leg. Scope: the ER multi-value movie leg ONLY; the movie canonical resolves by exact library name (JF-659 contract: the canonical IS the synced library name); dedupe by item id; >=2 distinct -> the existing yes/no cycling state machine. Design note: for movies ALWAYS ask on >=2 (no JF-885 dominance bypass) is the recommended first cut, because the live evidence shows Amazon ranks the sequel first and a wrong evening film costs more than a 2s confirm; the worker may keep arbitration only if it maps cleanly. Do NOT add raw-search movie disambiguation (unfiled behavior) and do NOT touch the artist path.

EXECUTION RECORD 2026-10-19 22:50 (orchestrator): worker dispatched 21:05, interrupted twice by session recycles (subagent lifetime bounded by the orchestrator's turn; both resumes kept the worktree intact), completed 22:26. Merge a7a5c854 pushed 22:48 after the deploy-gate hook correctly forced the gates into the SESSION transcript (the worker's own gates do not count for the session record): /simplify 4-agent battery + code-review high 8-angle both re-run in-session, findings applied (E1 the resolver-loop wall-clock budget, S2 pattern collapse, F5 it-IT test-locale default) or skipped with recorded reasons (call-site Take = documented house convention; shared query builder = deliberate Movie-only divergence; always-ask stands because an exact full-title span cannot multi-match a partial-synonym catalog). Full suite 6085/6085 both TFMs, build 0 warnings both TFMs. Deployed to minix AlexaSkill_1.0.0.0 (auto-update had NOT yet pulled 1.0.1.0; our dev DLL carries Version=1.0.1.0 in that dir), active-DLL md5 verified, config survived. Live verification boundary: the simulator endpoint cannot inject ER resolutions (flat slot strings), so the gate's live proof is the device probe (JF-911's morning line) and, when the simulate router recovers, the profile-nlu two-value ER reading plus the e2e row.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped 2026-10-19 night: the movie multi-value ER gate. MERGED a7a5c854 (worker 4ff61452 + in-session review fixes 9e65668a), CI green, deployed to minix (md5 e67a984e..., active DLL verified, loaded-assembly log Version=1.0.1.0 from AlexaSkill_1.0.0.0, config survived Users=1). WHAT LANDED: PlayVideoIntentHandler's PlayMovie branch resolves EVERY ER canonical via MultiValueErDisambiguation.ResolveErMovieCandidatesAsync (exact-name, Movie-only, library-filtered, id-deduped in rank order, ONE wall-clock 6s budget across the loop, capped at FirstMatchStateCap); >=2 distinct -> the existing video yes/no ask (AskFirstMatch, MediaTypeVideo); ==1 -> survivor adoption (a stale rank-1 canonical now plays the real movie); closed legs byte-identical; song-carrier delegation untouched (rank-1 only, pinned). ALWAYS ask on >=2 (no JF-885 dominance bypass, maintainer call). 9 new tests, 6085/6085 per TFM, 0 warnings both TFMs. GATES: worker ran its battery; the orchestrator re-ran /simplify (4 agents: E1 budget fix + S2 pattern applied; truncation/shape findings skipped as the documented house convention) and code-review high (8-angle: F2=E1 confirmed, F5 it-IT test-locale default applied; F1 bypass rejected - an exact full-title span cannot multi-match this catalog, the shared synonym is the bare token; F3/F4 skipped with reasons in 9e65668a). RESIDUALS -> JF-911 (ranks 4+ unreachable, no live case; e2e row when SMAPI returns). DEVICE PROBE for morning: 'chiedi a mia collezione di mettere il film gruffalò' must ask which one (naming the sequel first) instead of silently playing it; si plays rank-1, no advances.
<!-- SECTION:FINAL_SUMMARY:END -->
