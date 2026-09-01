---
id: JF-440
title: >-
  Promote the JF-439 song fallback to BaseHandler + sibling coverage
  (QueryArtistLibrary), consolidate the Search chain and the single-song play
  bookkeeping
status: Done
assignee:
  - zai
created_date: '2026-09-01 17:20'
updated_date: '2026-09-01 20:35'
labels:
  - code-review
  - consolidation
  - artist-search
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs:741
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/QueryArtistLibraryIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up task consolidating the altitude findings from the JF-439 code-review round (2026-09-01, all CONFIRMED, filed per the review-recommendation discipline; the JF-439 v1 scoped to one handler by its AC, these are the generalizations the review identified):

1. SIBLING COVERAGE: QueryArtistLibraryIntentHandler answers the identical bare NotFoundArtist from the identical AMAZON.Musician slot with no song fallback ('cosa abbiamo di sugar free jazz' dead-ends while the same slot value in PlayArtistSongs now plays the song).
2. THIRD PRIVATE COPY of the Search->SearchPhonetic chain (FindSong ~497, PlaySong ~273, JF-439 ~768) with three different index-readiness contracts; a future warming/flag semantics change lands in one copy and silently diverges the others. Feeds JF-382.
3. SINGLE-SONG PLAY BOOKKEEPING is now the 4th/5th inline copy (queue + FullNowPlayingItem + AudioPlayer + announcement); the copies disagree on crash-recovery persistence (only the artist path and the JF-439 path persist/clear, the FindSong/YesIntent/APL sites do not SetQueue).
4. JF-377 'no' exit: coin-flip inputs that weakly match one artist name exit through the yes/no prompt whose 'no' dead-ends in NoMoreMatches without trying the song index.

Below-cap items from the same review worth folding in: FakeSongIndex in tests duplicates FakeNgramIndex (PlaySongTitleFallbackTests:138); CrossMediaSongThreshold/artist mirror constants could live beside their BaseHandler siblings.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Promote the inverse cross-media song fallback from PlayArtistSongs-private to a BaseHandler helper (the TryEntityFallbackAsync pattern, ISongNgramIndex as method param), and wire QueryArtistLibraryIntentHandler (same bare NotFoundArtist from the same AMAZON.Musician slot: 'cosa abbiamo di sugar free jazz' dead-ends today)
- [x] #2 Extract the shared Search->SearchPhonetic lookup chain (currently 3 private copies with 3 different readiness contracts: FindSong double-gates, PlaySong block-gates, the JF-439 copy catches) into one ISongNgramIndex-level or Util-level helper - feeds JF-382's 'do not add a third copy' rule
- [x] #3 Single-song play builder: one BaseHandler shape for queue + FullNowPlayingItem + continuation-clear + AudioPlayer + announcement (currently the 4th/5th inline copy; the copies disagree on crash-recovery persistence), and normalize FindSong/YesIntent/APL single-song sites onto it
- [ ] #4 Optional follow-up from the review: the JF-377 yes/no 'no' exit (NoIntentHandler) does not try the song index for coin-flip inputs that weakly matched one artist name ('sugar free jazz' vs artist 'Free' -> AskFirstMatch -> 'no' -> NoMoreMatches without trying songs)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-01 additions from the JF-437 review round (all CONFIRMED, deferred with reasons): (F4) the word-coverage tier's result is INERT for SearchAsync consumers that re-score with FuzzyMatcher (TryEntityFallbackAsync 85-bar, PlayAlbum cross-media gates): a word-subset match scores 27 ('The Beatles' vs 'beatles live') below every gate, so the greedy-slot misroute family still not-founds there - fold a word-coverage-aware gate into the BaseHandler promotion (AC#1); (F5) tier 1.5 exists only in the in-memory branches: cold-window/disabled-index DB paths lack it ('beatles live' not-founds while cold, plays warm) - document or mirror; (F7-cache) the tier re-tokenizes the whole pool per call (~5-15ms/20k artists, only on tier-1 misses): precompute per-artist token sets in ArtistIndexService's load loop (SongNgramIndexService precedent); (F9) FOURTH parallel word-coverage primitive now exists (KeywordMatcher.Score loops, IsCoincidentalContainmentMatch, handler IsWordSubset, WordCoverageCandidates) with different tokenization/duplicate rules - extract one shared primitive when consolidating.

2026-09-01 DEPLOYED + LIVE-VERIFIED (a1c96f4, config survived, boot clean). New sibling green live: QueryArtistLibrary musician='screenwriters blues' -> 'Ho trovato il brano Screenwriter's Blues. Eccolo.' + play. Full regression matrix green (PlayArtistSongs fallback, tier 1.5 beatles live, FindSong elicitation).

DEFERRED (AC#4, optional): the JF-377 'no'-exit song try. NoIntentHandler would need the song index + a decision on which session state (disambig vs crossmedia) carries the original query; the two main paths (JF-439 + the sibling) already cover the observed coin-flip class, so the marginal case is an artist-weak-match ('sugar free jazz' vs artist 'Free') declining into NoMoreMatches. Re-open only if a live report shows the shape.

Deferred infrastructure items stay in these notes: F5 (DB-path tier divergence, documented at the JF-437 helper), F7 (precompute artist token sets in ArtistIndexService), F9 (fourth word-coverage primitive consolidation - KeywordMatcher.Score loops / IsCoincidentalContainmentMatch / handler IsWordSubset / WordCoverageCandidates).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-440: the cross-media fallbacks, the song-index lookup chain, and the single-song play bookkeeping each have ONE definition.

WHAT CHANGED (commit a1c96f4, 11 files, +345/-217)
- SongIndexSearch.SearchWithPhoneticFallback (new Util extension): the ONE index chain; was 3 private copies with 3 readiness contracts (FindSong double-gated, PlaySong block-gated, JF-439 caught). Unified: null/disabled -> empty, warming -> the index's own exception (entry-gated callers propagate, opportunistic fallbacks catch).
- BaseHandler.BuildSingleSongResponse: the ONE single-song shape (queue + bookkeeping + stale-continuation clear + AudioPlayer + announcement). FindSong (x2), YesIntent and the JF-439 fallback normalized onto it (4 inline copies gone).
- BaseHandler.TrySongFallback promoted from PlayArtistSongs-private (TryEntityFallbackAsync pattern, logLabel for handler-identifiable logs) + QueryArtistLibrary wired as the sibling: 'cosa abbiamo di <song title>' now serves the song with the announcement.
- TryEntityFallbackAsync word-coverage accept-gate (F4): a word-coverage match scores 27 < 85 on the fuzzy scale ('The Beatles' vs 'beatles live'), so the cross-media bar rejected exactly the qualifier class the JF-437 tier serves; the gate accepts at the NORMAL threshold (review round: no floor would let 'soft rock' -> artist 'Soft' auto-substitute at any score - the mood-slot wrong-substitution class).
- Review round real bug also fixed: the consolidated chains now RESOLVE the library filter to parent-chain roots - unresolved collection-folder ids silently no-op'ed the index stage for library-restricted users (present in FindSong/PlaySong all along, exposed by the consolidation review).

VERIFICATION (live, minix, config survived)
- The NEW sibling: QueryArtistLibrary 'screenwriters blues' -> 'Ho trovato il brano Screenwriter's Blues. Eccolo.' + play (log: QueryArtistLibrary-labeled fallback).
- Regressions all green: PlayArtistSongs fallback (same announcement), JF-437 tier 1.5 ('beatles live' 7ms -> The Beatles), FindSong keywords (correct elicitation flow), plus the full unit suite 2812 passed / 0 failed; Release 0 warnings.
- Gates: /simplify (4 findings applied incl. a 4th escaped single-song site and a doc-block collision); /code-review high (8 findings ALL applied: 2 real - the library-id domain bug and the floorless F4 gate - plus null short-circuit, untracked-file, log labels, doc restore, indentation).
- AC#4 (JF-377 'no' exit song try) DELIBERATELY DEFERRED: marked optional in the task; NoIntent wiring needs its own session-state analysis (the crossmedia attrs vs disambig state), filed as the remaining note below.
- DoD 6/8 N/A (no model/locale changes); 7 = the sibling test + the full regression matrix.

REMAINING (deferred with reasons, folded into the notes for a future pass): JF-377 'no'-exit song try (AC#4, optional); F5 DB-path tier divergence (documented in JF-437's helper); F7 token precompute in ArtistIndexService; F9 fourth-primitive consolidation.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining)
- [x] #11 Findings applied or tracked
<!-- DOD:END -->
