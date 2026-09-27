---
id: JF-645
title: >-
  JF-645 - JF-643 residuals: wire the remaining kana-reachable query sites, lift
  the genre tier to SearchService at first sibling, vocabulary TTL cache,
  symmetric normalization for kana-tagged libraries
status: To Do
assignee: []
created_date: '2026-09-27 08:01'
labels:
  - search
  - i18n
  - ja-JP
  - tech-debt
dependencies:
  - JF-643
references:
  - >-
    backlog/tasks/jf-643 -
    JF-643-katakana-query-values-never-match-Latin-library-names-script-gap-in-fuzzy-phonetic-search-naturalized-ja-JP-artist-and-genre-requests-all-end-not-found.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-643 /simplify round (four angles; findings consolidated here per the review-recommendation rule; the applied items are listed in JF-643's task notes).

1. WIRE THE REMAINING KANA-REACHABLE QUERY SITES (reuse angle; JF-643 wired the primary paths only). Genre slots with identical exact-Genres semantics, un-wired: PlayRandomIntentHandler (~:127), PlayByDecadeIntentHandler (~:126), BrowseLibraryIntentHandler (~:343, plus its SearchTerm at ~:226). Raw-kana SearchTerm sites, un-wired: AddToQueueIntentHandler song side (~:141; artist side already covered via ArtistSearch), PlayNextIntentHandler (~:141), AddSongToPlaylistIntentHandler (~:123), PlayChannelIntentHandler (~:81), TvNextUpService (~:108). Each is a one-line KatakanaRomanizer.Romanize at the slot-string entry, following the JF-643 per-site pattern (keep the raw string where speech/session still needs it).
2. LIFT ResolveGenreTagAsync WHEN THE FIRST SIBLING IS WIRED (reuse angle): the genre-vocabulary resolution tier is private to PlayByGenreIntentHandler; when item 1 wires PlayRandom/PlayByDecade/BrowseLibrary or PlayRadio's genre seed, hoist it onto the SearchService collaborator (the GetArtistSongsAsync precedent: one helper so the query shape stays consistent) instead of copying it per handler. The JF-382 'do not add a third copy' rule applies.
3. GENRE-VOCABULARY TTL CACHE (efficiency angle): ResolveGenreTagAsync encodes ~500 vocabulary entries per kana genre request with no caching; genre vocab is near-static. Cache the name-to-codes map the way SearchResultCache does (30-min TTL) or event-driven like the index services. Optional companion: for a kana slot, the initial romanized-exact query is a near-certain miss (jazu vs Jazz); skipping straight to the tier on ContainsKana saves one DB round-trip (the existing test pins audioQueries == [jazu, Jazz]; update it if this lands).
4. SYMMETRIC NORMALIZATION FOR KANA-TAGGED LIBRARIES (altitude angle; recorded in KatakanaRomanizer's class doc): asymmetric (query-only) normalization means a kana query against a KATAKANA-TAGGED library name now misses on every tier where it previously exact-matched. If native-script-tagged libraries matter (J-pop in the wild), the fix is romanizing kana-containing candidate text at ArtistIndexService/SongNgramIndex build time and in the matcher, preserving kana-kana exactness while still bridging kana-Latin.
5. SCRIPT COVERAGE SHAPE (altitude angle; from JF-643's own Description): the romanizer is kana-specific by construction; Devanagari (hi-IN) and Arabic (ar-SA) native-script values remain unmatched on every path. If a second script is needed, extract a normalizer-chain shape rather than growing a parallel one-off.

VERIFICATION BAR: for each wired site, a test pinning the katakana-in / Latin-match property in that handler's test file (the JF-643 pattern); suite green both TFMs; the lift (item 2) is behavior-preserving with PlayByGenre's existing tests unchanged.
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
