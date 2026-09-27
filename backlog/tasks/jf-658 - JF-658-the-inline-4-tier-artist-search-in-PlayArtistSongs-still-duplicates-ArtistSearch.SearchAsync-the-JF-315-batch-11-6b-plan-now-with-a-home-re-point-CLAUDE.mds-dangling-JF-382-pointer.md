---
id: JF-658
title: >-
  JF-658 - the inline 4-tier artist search in PlayArtistSongs still duplicates
  ArtistSearch.SearchAsync (the JF-315 batch-11 '6b' plan, now with a home);
  re-point CLAUDE.md's dangling JF-382 pointer
status: To Do
assignee: []
created_date: '2026-09-27 19:12'
labels:
  - refactor
  - tech-debt
  - search
dependencies: []
references:
  - >-
    backlog/tasks/jf-315 -
    Refactor-Decompose-the-2268-line-BaseHandler-God-class-into-injected-collaborators.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 from the backlog audit (JF-315 closed; this task is the home for the one plan that had none).

CONTEXT: PlayArtistSongsIntentHandler still carries its own inline 4-tier artist search (Fast/Thorough/Parallel mode selection), duplicating ArtistSearch.SearchAsync. The consolidation was planned twice inside JF-315's batch-11 notes (stopped twice at the batch boundaries); the full plan and the stopping history live in JF-315's notes (the '6b' section). The artist-SONGS query blocks are long consolidated into SearchService.GetArtistSongsAsync.

THE WORK: fold the inline 4-tier chain into ArtistSearch.SearchAsync per the 6b plan (the plan table in JF-315's notes is the work order; re-derive anything stale against the current tree). Retire the JF-643 romanization site at the inline chain's entry with it (ArtistSearch.SearchAsync's entry already romanizes; the JF-382 scope note in that task records the same).

ALSO in this edit: CLAUDE.md's Artist Search Fallback Chain section still says 'consolidate via JF-382', which now dangles (JF-382 was the coincidental-containment task, Done, and never held the inline-search plan); re-point it here.
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
