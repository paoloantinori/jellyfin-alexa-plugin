---
id: JF-524
title: >-
  Generalize DisambiguationHelper.ResolvePick off FindSongCandidate and delete
  the FindSong delegators (retarget the direct tests)
status: Done
assignee: []
created_date: '2026-09-08 12:23'
updated_date: '2026-09-08 14:53'
labels:
  - refactor
  - tech-debt
  - multi-turn
dependencies: []
references:
  - JF-407
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-407 item 2 simplify + review passes (2026-09-08). The pick-words machinery now lives in DisambiguationHelper, but two artifacts of the zero-churn move remain:

(1) ResolvePick is typed List<FindSongCandidate> (a Handler.Intent DTO) while it advertises itself as the numbered-candidate resolver shared by every DisambiguationHelper-based picker; the next picker with a different candidate record cannot call it. Generalize to candidate names (IReadOnlyList<string>) or a selector Func, keeping the tables internal. Then delete the thin delegators in FindSongIntentHandler (~660-663) and retarget the ~20 direct ResolvePick tests to the shared helper (the delegators were kept ONLY to avoid test churn in the zero-behavior move; both review passes flag them as the artifact to remove in the same change that retargets the tests).

(2) Cosmetic rider: the section rationale comment in DisambiguationHelper was consolidated under the banner in JF-407; keep future additions there.

Constraint: pure refactoring, no behavior change; the JF-395 negative-exit ordering (single-token negative before ResolvePick, multi-token after) is pinned by existing tests and must not move.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 ResolvePick generalized off FindSongCandidate (IReadOnlyList<string> candidateNames or a Func<T,int,string> selector) so DisambiguationHelper's other pickers (album/artist disambiguation flows) can use it; FindSongCandidate stays in FindSongSessionData.cs
- [ ] #2 The three thin delegators in FindSongIntentHandler (lines ~660-663) deleted and the ~20 direct ResolvePick tests in FindSongIntentHandlerTests retargeted to DisambiguationHelper (the delegators exist only to avoid test churn)
- [ ] #3 IsNegativeAnswer consumers evaluated: any picker that needs the JF-395 negative-exit gets it
- [ ] #4 Full suite green; /simplify + code-review high gates before merge
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implementation notes (worker, 2026-09-08, worktree refactor/jf524-resolvepick-generalize, uncommitted): signature now `internal static int? ResolvePick(string input, IReadOnlyList<string> candidateNames, string locale)`; TryMatchByTitle re-typed identically; `using ...Handler.Intent` removed from DisambiguationHelper; both FindSong delegators deleted (3 call sites call DisambiguationHelper directly, names projected via `Select(c => c.Name).ToList()`); 13 ResolvePick test methods (36 cases) moved verbatim to Tests/Handler/DisambiguationHelperTests.cs with name lists. Build 0 warnings; suite 3481/3481. Gates: /simplify 4 agents (2 findings applied: tombstone marker downgraded to plain comment, digit-check comment reworded away from FindSong's Take(4); 3 skipped with reasons below) + code-review 5 reviewers (0 findings).

Part-3 evaluation (pickers vs ResolvePick/IsNegativeAnswer adoption): all DisambiguationHelper flows are yes/no cyclers by design; follow-ups arrive only as AMAZON.YesIntent/NoIntent/FallbackIntent, none of which deliver free text to the skill, so ResolvePick has no input channel there and IsNegativeAnswer is redundant (negatives route Amazon-side to NoIntentHandler, which already implements advance/decline/NoMoreMatches). JF-420.2 (commit 8ce633c) explicitly chose the yes/no contract over numeric pick for the multi-artist flow; no adoption recommended anywhere.

Deferred observations from the /simplify altitude review, NOT adopted (each is behavior/scope change beyond JF-524): (1) the JF-395 negative-exit interplay still lives at the FindSong call site; a tri-state ResolvePick (pick/negative/no-match) would move that contract into the helper but changes FindSong flow code and its tests, and no adopter exists (previous point); (2) the `locale` parameter is unused by the resolver body (tables are cross-locale unions) but was kept because the task prescribes the signature and a future locale-aware variant may use it; (3) a `Func<T,string>` selector overload (FuzzyMatch idiom) was NOT added since there is no second caller today; add it with the first album/artist adoption.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped: ResolvePick generalized to IReadOnlyList<string> candidateNames (only Name was ever read; TryMatchByTitle re-typed likewise; the Handler.Intent using removed from DisambiguationHelper, zero FindSongCandidate references remain there); the two thin FindSong delegators deleted, its 3 call sites call the helper directly, JF-395 ordering untouched; 13 test methods (36 cases) moved verbatim to DisambiguationHelperTests. Picker evaluation (AC#3, analysis only): every other DisambiguationHelper picker is a yes/no cycler whose follow-ups arrive only as Yes/No/Fallback (no free-text channel), so adoption would require slot elicitation + dialog registration in 17 models - JF-420.2 already rejected numeric pick for exactly that reason; FindSong remains the sole free-text picker (its turn elicits titleKeywords). Gates: /simplify (implementer 4-agent pass, findings applied/skipped with reasons in the notes), code-review high via orchestrator dispatch (clone-based verification after a shell outage; reference inventory zero-dangling). INFRA INCIDENT documented: a per-user /tmp QUOTA exhaustion (12714/12715M, caused by workers each exporting their own duplicate NuGet cache copies, ~2.2GB) killed every long-running shell (the orchestrator's, the implementer's, two reviewers') and produced 55 spurious test reds on the merge run; root-caused, caches deduplicated, suite re-run green 3481/3481 on main post-merge (c09c5003). Behavior-identical refactor; rides the next functional deploy.
<!-- SECTION:FINAL_SUMMARY:END -->

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
