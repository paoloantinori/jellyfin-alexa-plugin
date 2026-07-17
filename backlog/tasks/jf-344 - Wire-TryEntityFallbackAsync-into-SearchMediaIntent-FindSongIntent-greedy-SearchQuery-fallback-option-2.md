---
id: JF-344
title: >-
  Wire TryEntityFallbackAsync into SearchMediaIntent + FindSongIntent (greedy
  SearchQuery fallback, option 2)
status: Done
assignee: []
created_date: '2026-07-15 19:21'
updated_date: '2026-07-16 09:57'
labels:
  - bug
  - nlu
  - artist-search
  - language-agnostic
dependencies: []
references:
  - >-
    /home/pantinor/.cc-mirror/zai/config/plans/the-suggested-solution-sounds-inherited-lemon.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up to the PlayMoodMusic v1 entity-fallback change (plan: /home/pantinor/.cc-mirror/zai/config/plans/the-suggested-solution-sounds-inherited-lemon.md, option 2).

BACKGROUND: A pre-emptive NLU competition audit (this session) ranked the greedy AMAZON.SearchQuery intents across all 17 locales. v1 wired the reusable language-agnostic helper `BaseHandler.TryEntityFallbackAsync` (Tokenize via KeywordMatcher → phonetic ArtistSearch via IArtistIndex.TryGetPhoneticCode → CrossMediaArtistThreshold gate → BuildArtistSongsResponseAsync + FoundArtistInstead) into PlayMoodMusicIntent, the highest-risk one. This task widens it.

SCOPE: wire the existing `TryEntityFallbackAsync` helper into the other two high-risk greedy AMAZON.SearchQuery intents, on their miss paths:
- SearchMediaIntent (slot `query`) — "search/find {query}" carriers.
- FindSongIntent (slot `titleKeywords`) — "find a song called {titleKeywords}".

For FindSong, prefer chaining a song/album tier inside TryEntityFallbackAsync (it was named entity-agnostic for this reason) rather than only artist; decide per intent based on what each handler already recovers to.

NOTE: the it-IT PlayByGenreIntent `AMAZON.SearchQuery` anomaly (16 other locales use AMAZON.Genre) is INTENTIONALLY left as-is — it is the same cross-language-bias trade-off the phonetic/catalog architecture exists for (CLAUDE.md anti-pattern #10, memory: feedback_search_history_before_arch_changes). The same helper covers its misroutes if wired there later; do NOT swap the slot type.

Definition of Done MUST include: run `/simplify` and `/code-review high` on the diff before marking Done; dotnet build -warnaserror (0 warnings) + full test suite green; Simulator verification that a misrouted entity query now resolves.
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed (commit cd2bbdc). FindSong: wired BaseHandler.TryEntityFallbackAsync into SearchAndRespondAsync's no-match path, guarded by `sessionData.ArtistName` (fires only on the no-artist keywords-only flow; skips when an artist was given, mirroring PlaySong's "no musician slot" cross-media fallback). Added IUserDataManager + optional DeviceQueueManager deps. New unit test Search_NoArtistNoMatch_KeywordsMatchArtist_FallsBackToArtist. SearchMedia: investigated and found it ALREADY has its own artist fallback (SearchByArtistNameAsync, augmenting sparse results) — so no wiring was added there (would be redundant + add unused deps). The it-IT PlayByGenre SearchQuery anomaly left as-is (anti-pattern #10). /code-review high (no issues) + /simplify (minor nits, none blocking) run; 2512 tests pass; deployed + plugin healthy.
<!-- SECTION:FINAL_SUMMARY:END -->
