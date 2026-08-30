---
id: JF-416
title: >-
  FindSong disambiguation: deduplicate candidates by name (3 identical "The
  Idiot Kings" offered) and fix double-spoken list
status: To Do
assignee: []
created_date: '2026-08-30 12:39'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live finding 2026-08-30 (on-device, user-reported): FindSong disambiguation with keywords "idiot" returned "Ho trovato 4 canzoni. 1. The Idiot Kings, 2. The Idiot Kings, 3. The Idiot Kings, 4. American Idiot" - three identical entries with zero informational difference for the user, who cannot choose between them. Root cause: the scored.Take(4) at FindSongIntentHandler.cs:598 does not deduplicate by name; the same song from different albums (or the same song matched by different scoring paths) appears multiple times.

Second bug in the same code: the prompt double-speaks the candidate list. FindSongFoundMultiple already receives candidateNames as a format arg, but fullPrompt (line ~607) appends candidateNames AGAIN, so the user hears the full list twice (and the reprompt repeats it a third time).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Deduplicate FindSong disambiguation candidates by song NAME (case-insensitive): when multiple items share the same name (same song from different albums/versions), keep only one representative in the top-4 list. Selection criteria for the representative: prefer the most-played (UserData.PlayCount), tiebreak by first occurrence in the scored list.
- [ ] #2 The deduplication must preserve the original scored ORDER (highest score first) among the deduplicated survivors.
- [ ] #3 If deduplication reduces the list below 4, fill remaining slots from the next-highest-scoring non-duplicate items (up to the original Take(4) window, i.e. scan up to the first 8 scored items to find up to 4 unique names).
- [ ] #4 Fix the double-speaking: FindSongFoundMultiple already receives candidateNames as a format arg, but fullPrompt appends candidateNames AGAIN. Remove the duplicate append.
- [ ] #5 Unit tests: (a) 3 items with same name + 1 different -> disambiguation shows 2 entries, not 4; (b) items with distinct names -> unchanged behavior; (c) the prompt does not contain the candidate list twice.
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
