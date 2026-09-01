---
id: JF-420
title: >-
  Disambiguate containment-vs-full-name artist matches ('P!nk floyd': offer P!nk
  AND Pink Floyd instead of auto-playing P!nk)
status: Done
assignee:
  - zai
created_date: '2026-08-31 06:04'
updated_date: '2026-09-01 11:52'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
JF-417 residual (2026-08-31 on-device): the bare imperative forms (JF-418) successfully route 'suonare pink floyd' to PlayArtistSongsIntent with musician='P!nk floyd'. The artist search then resolves to P!nk (4 chars) via the containment exemption at ContainmentScore (90), beating Pink Floyd (10 chars) which would score ~85 through genuine fuzzy matching. The tier-4 exclusion approach (JF-417 first attempt) was reverted because it broke 'nirvana unplugged'.

The correct UX: when both P!nk and Pink Floyd are plausible matches for 'P!nk floyd', present BOTH as a disambiguation ('Ho trovato P!nk e Pink Floyd. Quale?') instead of auto-playing P!nk. This handles the string-indistinguishability: the user decides whether 'floyd' is part of the artist name or a qualifier.

Design: at the point where the artist search returns a single match that was scored via the containment exemption (candidate contained in query, score == ContainmentScore), AND the query is multi-word, check if there are other artists that fuzzy-match the full query above threshold. If so, return both for disambiguation.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 When PlayArtistSongsIntent's artist search resolves to a candidate via the containment exemption (candidate name is a substring of the query, score == ContainmentScore), AND the query is multi-word, AND there exists a DIFFERENT artist in the library whose name fuzzy-matches the FULL query at a score >= DefaultThreshold, present BOTH as disambiguation candidates to the user instead of auto-playing the containment match
- [ ] #2 The disambiguation uses the existing DisambiguationHelper pattern (numbered list, user picks 1/2/3 or says 'no')
- [ ] #3 No-regression: 'nirvana unplugged' (containment match IS the correct answer, no better full-name alternative) must auto-play Nirvana without prompting
- [ ] #4 No-regression: single-word queries are unaffected
- [ ] #5 No-regression: queries where the containment match is the ONLY match auto-play without prompting
- [ ] #6 Unit tests: (a) P!nk floyd with both P!nk and Pink Floyd -> disambiguation prompt; (b) nirvana unplugged with only Nirvana -> auto-play; (c) single-word containment -> unchanged; (d) containment-only match -> auto-play
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. At the point where PlayArtistSongsIntentHandler gets a single artist match, check if the match was resolved via the containment exemption (the JF-417 deferred tier-2 path or tier-4 containment).
2. If yes AND the query is multi-word, search ALL artists for a DIFFERENT artist that fuzzy-matches the full query above DefaultThreshold.
3. If found, present both as disambiguation candidates (reuse DisambiguationHelper.AskFirstMatch pattern with numbered list).
4. No-regression guards: single-word queries, only-one-match, and nirvana-unplugged (no full-name alternative) must auto-play unchanged.
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-420 (parent) closed: the containment-vs-full-name artist selection is fair, symmetric, and honest end to end.

All three subtasks landed with gates:
- JF-420.1 (14eb2a8): exact artist-name matches bypass the gate (equality is the degenerate containment case; live Soul Coughing incident).
- JF-420.3 (42ad2a3): the margin is symmetric and drift-free - FuzzyMatcher.ApplyFairLengthPenalty single-sources the exemption-free penalty, FairComparisonScore scales both sides by the bidirectional length fraction (the matcher's 0.5 floor is a recall device that manufactured phantom margins in the gate), FuzzyMatcher.Score + full ranking fix the early-exit masking ('Floyd' hid 'Pink Floyd'), exemption-only alternatives cannot clear the 80 bar, redundant shorter forms ('Miles' vs 'Miles Davis') skip the comparison.
- JF-420.2 (8ce633c): the ambiguous branch's prompt speaks the yes/no flow the state machine supports (reworded in all 17 locales, no numbered list), and all disambiguation session-attr keys (including the crossmedia family) are single-defined constants.

Deployed: JF-420.1 live on minix since 2026-08-31 (verified: exact queries auto-play); 420.2/420.3 ride the 2026-09-01 bundle.
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
