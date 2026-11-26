---
id: JF-976
title: >-
  Hoist the four inline album-disambiguation blocks into ONE shared helper
  (parameterized: media type, not-found string, kana predicate)
status: To Do
assignee: []
created_date: '2026-11-26 20:54'
labels:
  - refactor
  - album-disambiguation
  - tech-debt
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-974 code-review round (2026-11-26, same-turn landing rule): the ~90-line multi-match disambiguation block (kana-bar narrowing with refusal logging, FuzzyMatch pre-check + HasFullKeywordCoverage gate, the _handleFuzzyMiss composition delegate, AskFirstMatch fallback) now exists as FOUR near-verbatim inline copies: the PlayAlbum title path, TryAlbumFallbackAsync, the playlist shuffle arm, and the new album-shuffle arm (AlbumPlayService.cs ~1532). This is subtle acceptance logic, not boilerplate; a fix to the kana refusal shape, the coverage gate, or the miss-outcome handling applied to one copy silently misses the others (the exact "missed one" class). The hoist threshold the project uses (second copy = hoist point) is long passed. Deeper change: one shared helper parameterized over media type, not-found string, and kana acceptance predicate, consumed by all four sites. Do it as its own change with its own gate round: the block is subtle enough that the hoist must not ride a feature diff.
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
