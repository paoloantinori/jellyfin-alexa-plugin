---
id: JF-532
title: >-
  FindSong: encode the prompt-state invariant (state-explicit ElicitAnswer
  helper replacing the byte-identical elicit pair); shared artist-scoped test
  mock helper
status: Done
assignee: []
created_date: '2026-09-09 21:34'
updated_date: '2026-09-10 10:41'
labels:
  - refactor
  - multi-turn
  - find-song
  - tech-debt
dependencies: []
references:
  - JF-530
  - JF-413
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-530 simplify state-audit (2026-09-09): the JF-530 fix corrected two prompt/state desync sites, but the invariant 'State agrees with the prompt's meaning' is enforced NOWHERE structurally - FindSongSessionData.State is the only signal telling the next turn what the captured answer means, and setting it remains a manual side-action decoupled from choosing the prompt. ElicitTitleKeywords and ElicitArtist are byte-identical expressions (both elicit the titleKeywords slot, the deliberate AMAZON.SearchQuery design), so nothing in the elicit call itself distinguishes keywords-prompts from artist-prompts. JF-530 was exactly one forgotten side-action; the class stays open for future sites.

TRAP to encode (from the audit): ElicitTitleKeywords cannot self-set AwaitingKeywords because HandleDisambiguatingAsync uses it for pick re-prompts where State must stay Disambiguating - the explicit nextState parameter is what makes a shared helper safe.

Test rider: the two new JF-530 tests duplicate ~28 lines of mock setup verbatim (4th/5th instance of the artist-scoped callback family) - extract a shared helper.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Consolidate ElicitTitleKeywords/ElicitArtist into one state-explicit ElicitAnswer(prompt, sessionData, nextState) helper that assigns State before building; every call site declares the answer's meaning - no forgotten side-action possible
- [ ] #2 Handle the disambiguation pick re-prompts correctly: they must pass nextState=Disambiguating (the trap that makes ElicitTitleKeywords self-setting wrong - document it on the helper)
- [ ] #3 ~13 mechanical call-site edits in one file, zero behavior change; the shared invariant comment from the JF-530 sites collapses into the helper's doc
- [ ] #4 Tests: extract the shared artist-scoped mock setup helper (2 verbatim copies in the JF-530 tests, 5th/4th instance of the family); full suite green
- [ ] #5 /simplify + code-review high gates before merge
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Simplify-gate note (2026-09-10, JF-533 efficiency agent): while in this file's test helpers, also drop the redundant GetUserById setup in SetupJellyfinUser (FindSongIntentHandlerTests ~line 2048): the specific _userId registration is immediately subsumed by the following It.IsAny<Guid>() registration (Moq last-registered-wins). Pre-existing, cosmetic.

Scope overlap resolved: JF-533's /simplify pass hoists the 3x-pasted discriminating GetItemList lambda (isArtist/hasArtistIds/isAudioMedia) into a private SetupArtistThenArtistScopedSongs-style helper + a CreateArtist factory in this test file; if that hoist lands with JF-533, this task's 'shared artist-scoped test mock helper' item is already done - verify at execution time instead of redoing it.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed 2026-09-10 with merge f84f69a9 (deployed in the JF-534 batch deploy). ElicitAnswer(prompt, sessionData, nextState) replaces the byte-identical ElicitTitleKeywords/ElicitArtist pair; 13 call sites each declare the answer's meaning; the disambiguation-pick trap (pass Disambiguating) documented on the helper; 7 manual State assignments folded, 6 self-assignments made explicit. Gates: /simplify (2 combined angles: clean + 1 doc-dedupe applied - the duplicated SearchQuery rationale now a pointer to BuildElicitSlotResponse's doc), code-review high ZERO findings (all 13 sites reconciled: 7 folded + 6 dispatch-precondition self-assignments; the musician-branch State move proven unobservable incl. exception paths; songNameContains null-default byte-identical for the 3 pre-existing callers), tests 3560/3560 with zero expectation edits (base 6160776d also 3560, delta exactly zero - the dispatch's '3558 baseline' was stale, worker proved it by re-running the base). Test rider complete: 2 remaining mock copies folded onto SetupArtistThenArtistScopedSongs; redundant GetUserById setup dropped. AC#4's 'shared artist-scoped test mock helper' was already done by JF-533's simplify pass (verified, not redone).
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
