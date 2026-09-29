---
id: JF-645
title: >-
  JF-645 - JF-643 residuals: wire the remaining kana-reachable query sites, lift
  the genre tier to SearchService at first sibling, vocabulary TTL cache,
  symmetric normalization for kana-tagged libraries
status: To Do
assignee: []
created_date: '2026-09-27 08:01'
updated_date: '2026-09-27 19:12'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-27 code-review round additions: (F1 polish) romaji reaches SPEECH and one session attribute on kana miss/recovery paths at these sites: PlayBook:126, PlayPodcast:187, PlayVideo:164, PlaySong:218/310/348, PlayAlbum:312/357/484/549, AlbumPlayService:736, BaseHandler:1233/1266 (the shared HandleFuzzyMiss strings, so even raw-preserving handlers emit romaji there), CrossMediaFallback:282 (crossmedia_notfound_query, spoken on the NoIntent decline turn). The fix shape is FindSong's raw-local pattern (raw for speech, romanized for matching). Judged acceptable for now: fires only for kana input whose pre-change outcome was total failure. (F2) ContainsKana's range U+30A1-30FF includes the middle dot U+30FB and prolonged mark U+30FC (a Latin+middle-dot string like Pop・Rock fires the tier; outcome still strictly better than not-found) and excludes halfwidth katakana U+FF66-FF9F (unromanized, coverage gap only). F3 (hardcoded threshold) was FIXED in the merge path (GetDefaultThreshold(user)).

2026-09-27 audit correction (same as JF-642's F4-hold supersession): PlayRandom (:92) and PlayByDecade (:130) now read the ER canonical at their Genres feeds (JF-642 round 2), so item 1's 'genre slots with identical exact-Genres semantics, un-wired' no longer describes them - what remains for the siblings is the kana resolution TIER for ER_NO_MATCH long-tail. Still accurate and verified un-wired: BrowseLibrary (genre filter ~:343 raw, SearchTerm ~:226) and the raw-kana SearchTerm sites (AddToQueue song side, PlayNext, AddSongToPlaylist, PlayChannel, TvNextUpService). Items 2-5 unchanged.

2026-09-29 JF-658 review finding filed here (this task's symmetric-normalization item is the tracked home): the JF-658 fold routed PlayArtistSongs' ER canonical through ArtistSearch.SearchAsync's entry romanization for the first time (the inline chain fed it verbatim). For a KANA canonical (a kana-tagged artist in an otherwise Latin library, the mixed-library shape) this both loses the pre-fold exact-self-match (the JF-643 narrowing, already accepted) and OPENS reachability the inline chain never had: the romanized query ('クイーン' -> 'kuin') can now weakly fuzzy-hit a LATIN artist (the 'kuin'->Keane-at-91 class JF-652 was built to block) while the JF-652 kana bar stays inert for canonical-bearing queries (the JF-659 invariant: kanaOrigin=false whenever a canonical resolved, sound for Latin canonicals because the ER match IS the collision evidence, unsound for kana canonicals whose evidence points at the kana-named artist the romanized search can no longer find). Pre-fold the same query missed every tier (honest not-found). Zero test pins on the kana-canonical shape today (all canonical fixtures are Latin). The symmetric index-side normalization this task tracks (index kana names alongside romaji at build time) closes both legs at once; alternatively scope a kana-canonical-specific bar to this shape. Not fixed inside JF-658: both options are behavior changes beyond a no-behavior-change consolidation fold.
<!-- SECTION:NOTES:END -->
