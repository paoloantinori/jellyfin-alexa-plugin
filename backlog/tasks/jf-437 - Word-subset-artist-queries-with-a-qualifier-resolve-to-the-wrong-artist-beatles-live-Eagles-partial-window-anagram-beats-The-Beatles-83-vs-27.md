---
id: JF-437
title: >-
  Word-subset artist queries with a qualifier resolve to the wrong artist
  ('beatles live' -> Eagles: partial-window anagram beats 'The Beatles' 83 vs
  27)
status: To Do
assignee: []
created_date: '2026-09-01 12:00'
labels:
  - search-quality
  - artist-search
  - live-finding
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/FuzzyMatcher.cs:349'
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs:186
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Util/ArtistSearch.cs:82'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live finding 2026-09-01 (deploy verification of the JF-420.2/420.3/421 bundle): simulator query 'beatles live' on the real library resolves to Eagles (log: matched artist='Eagles'), not The Beatles. Verified arithmetic via a temporary dump test (FuzzyMatcher.Score on the live names): Eagles=83, The Beatles=27, Beatles-bare=90.

ROOT CAUSE (confirmed): multi-word queries where the intended artist's name is a WORD-SUBSET of the query but neither a contiguous substring nor a prefix fall through tiers 1-3 (no name contains 'beatles live'; 'The Beatles' does not START with 'beatles') into tier-4 fuzzy, where PartialRatio's sliding window ranks a near-anagram short name above the real artist: the 'eatles' window inside 'beatles live' is edit-distance 1 from 'eagles' (score 83, length ratio exactly 0.5 so ApplyLengthPenalty's floor leaves it unpenalized), while 'The Beatles' scores 27 because the article prefix misaligns every window. Class members: 'beatles live', any '<artist> live/unplugged/acoustic' where the artist is stored with an article prefix; 'miles davis live' only survives today via tier-2 prefix luck.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 DIAGNOSE FIRST: reproduce with a permanent unit test (library [The Beatles, Eagles], query 'beatles live') capturing the confirmed arithmetic - Eagles 83 vs The Beatles 27 via FuzzyMatcher.Score
- [ ] #2 FIX (primary): a word-coverage tier in the artist search chain - candidates whose word-set (leading-article-stripped, stop-word-stripped) is a SUBSET of the query's word-set outrank partial-fuzzy candidates. 'beatles live' -> The Beatles; 'miles davis live' -> Miles Davis without tier-2 prefix luck. Generalize JF-420.3's IsWordSubset (handler-private today)
- [ ] #3 GUARDS: word-subset single matches still flow through the existing downstream judgment (JF-377 downgrade, JF-420 gate); no regression on 'nirvana unplugged', single-word queries, JF-381 band semantics
- [ ] #4 BOTH implementations or a documented reason: inline PlayArtistSongs chain AND ArtistSearch.SearchAsync stay in sync (JF-382 status quo)
- [ ] #5 Consider (secondary): article-aware prefix/partial normalization as complement - weigh against touching every fuzzy comparison
- [ ] #6 Live verify after deploy: 'beatles live' resolves to The Beatles (or its disambiguation), never Eagles
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
