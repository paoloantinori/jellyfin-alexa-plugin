---
id: JF-707
title: >-
  JF-707 - cap the multi-value ER artist ask's spoken list (long first-word
  families speak every member in one breath)
status: To Do
assignee: []
created_date: '2026-10-02 11:05'
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
