---
id: JF-420
title: >-
  Disambiguate containment-vs-full-name artist matches ('P!nk floyd': offer P!nk
  AND Pink Floyd instead of auto-playing P!nk)
status: To Do
assignee: []
created_date: '2026-08-31 06:04'
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
