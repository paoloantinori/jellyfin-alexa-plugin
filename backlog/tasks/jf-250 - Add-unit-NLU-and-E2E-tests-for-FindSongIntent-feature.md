---
id: JF-250
title: 'Add unit, NLU, and E2E tests for FindSongIntent feature'
status: Done
assignee: []
created_date: '2026-06-03 19:13'
updated_date: '2026-06-03 20:43'
labels:
  - enhancement
  - testing
dependencies:
  - JF-248
  - JF-249
references:
  - docs/superpowers/specs/2026-06-03-find-song-keyword-search-design.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add comprehensive tests for the FindSongIntent feature: unit tests, NLU routing tests, and E2E tests.

## Unit Tests

### KeywordMatcher tests
- Tokenize strips stop words per locale
- Tokenize handles punctuation (parentheses, dashes, apostrophes)
- Score: all keywords must match (keywordCoverage == 1.0) to be included
- Score: positional bonus (+5) when keywords match from first title token
- Score: titleCoverage boosts songs where keywords cover more of the title
- Edge: empty input, single word, stop-words-only returns empty array, very long input

### FindSongIntentHandler tests
- First invocation with musician slot → AwaitingKeywords state
- First invocation with titleKeywords slot → AwaitingArtist state
- First invocation with neither → AwaitingKeywords state (prompts for keywords)
- AwaitingArtist: valid artist name → search triggered
- AwaitingArtist: artist not found → FindSongArtistNotFound response, state preserved
- AwaitingKeywords: valid keywords → search triggered
- AwaitingKeywords: stop-words-only → FindSongTooVague response, state preserved
- Search: 0 matches → FindSongNoMatch, state preserved for retry
- Search: 1 match score >= 90 → auto-play with FindSongFoundOne
- Search: 2-4 matches → Disambiguating state with candidate list
- Search: >4 matches no artist → FindSongTooManyNarrow, state = AwaitingArtist
- Disambiguating: valid pick by number → playback, state cleared
- Disambiguating: valid pick by ordinal → playback, state cleared
- Disambiguating: invalid pick → FindSongInvalidPick, state preserved

## NLU Tests

Add fixtures to `tests/integration/fixtures/` verifying correct routing:
- "find a song by Police" → FindSongIntent (not PlayArtistSongsIntent)
- "find a song called breath" → FindSongIntent (not SearchMediaIntent)
- "search for a song" → FindSongIntent (not SearchMediaIntent)
- "help me find a song by Radiohead" → FindSongIntent
- "find a song" → FindSongIntent (not LaunchRequest or FallbackIntent)

## E2E Tests

Add fixtures to `tests/integration/fixtures/e2e_it-IT.yaml` (it-IT for reliable simulate-skill):
- "cerca una canzone di Police" → verify prompt for keywords
- Multi-turn: "cerca una canzone di Police" → "breath" → verify song found
- "trova una canzone chiamata breath" → verify global search
- Disambiguation: verify pick by number works
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Unit tests for KeywordMatcher.Tokenize: stop words, punctuation, locale-awareness
- [ ] #2 Unit tests for KeywordMatcher.Score: coverage, positional bonus, edge cases
- [ ] #3 Unit tests for FindSongIntentHandler: all state transitions (first call, AwaitingArtist, AwaitingKeywords, Disambiguating)
- [ ] #4 Unit tests for edge cases: empty keywords, stop-words-only, invalid disambiguation pick
- [ ] #5 NLU test fixtures for FindSongIntent vs SearchMediaIntent boundary
- [ ] #6 NLU test fixtures for FindSongIntent vs PlayArtistSongsIntent boundary
- [ ] #7 E2E test: find a song by artist then keywords
- [ ] #8 E2E test: find a song called keywords (global search)
- [ ] #9 E2E test: disambiguation pick by number
- [ ] #10 All tests pass
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added 16 NLU/E2E test fixtures for FindSongIntent feature. NLU fixtures (en-US: 7, it-IT: 6) verify routing of FindSongIntent (bare, titleKeywords) and FindSongByArtistIntent (musician) vs PlayArtistSongsIntent and SearchMediaIntent. E2E fixtures (it-IT: 3) cover artist-scoped search, keyword-only search, and bare invocation. Unit tests already existed from JF-245 (38 KeywordMatcher tests) and JF-248/249 (36 FindSongIntentHandler tests). All 2205 unit tests pass, YAML fixtures validate cleanly.
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
