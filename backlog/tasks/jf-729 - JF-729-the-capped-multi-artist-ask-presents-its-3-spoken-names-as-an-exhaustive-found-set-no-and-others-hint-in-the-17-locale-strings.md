---
id: JF-729
title: >-
  JF-729 - the capped multi-artist ask presents its 3 spoken names as an
  exhaustive found-set (no "and others" hint in the 17 locale strings)
status: To Do
assignee: []
created_date: '2026-10-03 12:10'
labels:
  - ux
  - disambiguation
  - localization
dependencies:
  - JF-707
references:
  - >-
    backlog/tasks/jf-707 -
    JF-707-cap-the-multi-value-ER-artist-ask-spoken-list-long-first-word-families-in-one-breath.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 same-turn from the JF-707 code-review high gate (finding 1 of 4, the
only behavior-facing one; findings 2 to 4 were applied inside JF-707 itself). JF-707
caps the multi-artist ask's SPOKEN list at `DisambiguationHelper.MultipleArtistsSpeakCap`
(3) while the cycling state keeps the full resolved list, so a 4-plus-artist
same-first-word family is reachable only by saying "no" past names the user already
heard. The 17 `DisambiguateMultipleArtists` locale strings were NOT adapted: the
prompt still reads "I found multiple artists: A, B, C. Shall I play the first one?
Say no for the next.", which presents the three spoken names as an exhaustive
found-set. A user who wants rank 4 or later and reads the list as complete gives up
or rephrases instead of cycling; the reprompt's "say no for the next" is the only
signal that anything follows, and it does not say how many.

THE WORK: a locale-string change across all 17 locales signaling that more candidates
exist when the resolved list exceeds the speak cap. Shape options to weigh: (a) an
"and N more" tail fed as a second format arg by `DisambiguationHelper.AskMultipleArtists`
(empty or absent at or below the cap, so the two-name shape stays byte-identical); (b)
a standalone conditional string key chosen when `matches.Count > MultipleArtistsSpeakCap`.
Either way: ResponseStrings plumbing, all 17 locale JSONs, `validate_locales.py`
(baseline-aware) green, and a pin asserting the hint appears above the cap and NOT at
or below it. The cycling contract itself is already pinned by JF-707's
`PlayArtistSongs_MultiValueEr_FourInLibrary_SpeaksThree_CyclesToTheFourth`. Deliberately
NOT done inside JF-707: that task's decided scope was the cap plus its pin; a 17-locale
string pass with its own validation is a separate work item.
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
