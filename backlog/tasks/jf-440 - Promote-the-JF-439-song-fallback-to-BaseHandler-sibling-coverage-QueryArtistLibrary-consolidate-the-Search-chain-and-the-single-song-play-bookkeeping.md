---
id: JF-440
title: >-
  Promote the JF-439 song fallback to BaseHandler + sibling coverage
  (QueryArtistLibrary), consolidate the Search chain and the single-song play
  bookkeeping
status: To Do
assignee: []
created_date: '2026-09-01 17:20'
updated_date: '2026-09-01 19:47'
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
- [ ] #1 Promote the inverse cross-media song fallback from PlayArtistSongs-private to a BaseHandler helper (the TryEntityFallbackAsync pattern, ISongNgramIndex as method param), and wire QueryArtistLibraryIntentHandler (same bare NotFoundArtist from the same AMAZON.Musician slot: 'cosa abbiamo di sugar free jazz' dead-ends today)
- [ ] #2 Extract the shared Search->SearchPhonetic lookup chain (currently 3 private copies with 3 different readiness contracts: FindSong double-gates, PlaySong block-gates, the JF-439 copy catches) into one ISongNgramIndex-level or Util-level helper - feeds JF-382's 'do not add a third copy' rule
- [ ] #3 Single-song play builder: one BaseHandler shape for queue + FullNowPlayingItem + continuation-clear + AudioPlayer + announcement (currently the 4th/5th inline copy; the copies disagree on crash-recovery persistence), and normalize FindSong/YesIntent/APL single-song sites onto it
- [ ] #4 Optional follow-up from the review: the JF-377 yes/no 'no' exit (NoIntentHandler) does not try the song index for coin-flip inputs that weakly matched one artist name ('sugar free jazz' vs artist 'Free' -> AskFirstMatch -> 'no' -> NoMoreMatches without trying songs)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-01 additions from the JF-437 review round (all CONFIRMED, deferred with reasons): (F4) the word-coverage tier's result is INERT for SearchAsync consumers that re-score with FuzzyMatcher (TryEntityFallbackAsync 85-bar, PlayAlbum cross-media gates): a word-subset match scores 27 ('The Beatles' vs 'beatles live') below every gate, so the greedy-slot misroute family still not-founds there - fold a word-coverage-aware gate into the BaseHandler promotion (AC#1); (F5) tier 1.5 exists only in the in-memory branches: cold-window/disabled-index DB paths lack it ('beatles live' not-founds while cold, plays warm) - document or mirror; (F7-cache) the tier re-tokenizes the whole pool per call (~5-15ms/20k artists, only on tier-1 misses): precompute per-artist token sets in ArtistIndexService's load loop (SongNgramIndexService precedent); (F9) FOURTH parallel word-coverage primitive now exists (KeywordMatcher.Score loops, IsCoincidentalContainmentMatch, handler IsWordSubset, WordCoverageCandidates) with different tokenization/duplicate rules - extract one shared primitive when consolidating.
<!-- SECTION:NOTES:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining)
- [ ] #11 Findings applied or tracked
<!-- DOD:END -->
