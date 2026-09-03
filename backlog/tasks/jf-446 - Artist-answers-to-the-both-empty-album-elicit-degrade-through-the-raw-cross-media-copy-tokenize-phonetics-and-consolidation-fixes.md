---
id: JF-446
title: >-
  Artist answers to the both-empty album elicit degrade through the raw
  cross-media copy (tokenize, phonetics, and consolidation fixes)
status: Done
assignee: []
created_date: '2026-09-01 23:28'
updated_date: '2026-09-03 00:33'
labels:
  - code-review
  - dialog
  - cross-media
  - tech-debt
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/KeywordMatcher.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review findings from JF-422 (2026-09-02, high effort, 1-vote verified). JF-422 made PlayAlbum's both-empty elicit ask for the ALBUM slot. The design relies on a documented mitigation: an artist answer ("koop") lands in the album slot and the cross-media artist fallback still plays the artist. The review falsified the strong form of that claim: the fallback PlayAlbum uses is a RAW INLINE COPY of the shared gate, not the shared helper, so the mitigation only holds for short, article-free, high-scoring names.

Findings (file: Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs):
1. Line ~309: the word-count guard counts RAW words (album.Split). "di pink floyd" / "the flaming lips" / "un disco di Koop" count 3+ > CrossMediaArtistMaxWords=2 and return terminal NotFoundAlbumByName. The shared BaseHandler.TryEntityFallbackAsync (~line 2626) runs KeywordMatcher.Tokenize FIRST (strips di/del/della/un/il/la + English stop words) and passes the same text. The raw text also goes to ArtistSearch whose in-memory tier-1 is a.Name.Contains(query), so "di koop" misses every tier even past the guard.
2. Line ~329: acceptance is non-phonetic FuzzyMatcher >= Math.Max(60, CrossMediaArtistThreshold=85); the removed musician-slot path used the phonetic 4-tier ArtistSearch gated at 60. ASR-drifted names in [60,85) ("cup" for "Koop") get a Confirm offer or not-found instead of playing. On success it plays the artist's SONGS (BuildArtistSongsResponseAsync), not the JF-411 most-tracks album.
3. Line ~233: an artist answer is searched as an album TITLE first; a word-match against any library album title (self-titled albums, cross-artist title collisions) auto-plays that album before the artist fallback can run. Library-dependent; the mitigation only engages on a search miss.
4. Test gap: no test drives the album-slot-with-artist-name + musician-empty shape for a >2-word answer (the dead-end of finding 1). CrossMediaTypeFallbackTests.PlayAlbum_NoAlbums_NoMusician_ArtistExists_FallsBackToArtist covers only the 1-word passing case.
5. Line ~259: the fuzzy full-catalog scan uses BuildAlbumQuery's DtoOptions(true) with no Limit, materializing full DTOs only to read a.Name. The same file already has the cheap precedent (GetAlbumTrackCountsAsync: DtoOptions(false) + EnableImages/EnableUserData/AddCurrentProgram=false).
6. Root cause of 1+2: PlayAlbum and PlaySongIntentHandler (~line 310) each carry a raw-split copy of the gate while BaseHandler.TryEntityFallbackAsync tokenizes and has a WordCoverageCandidates acceptance valve the copies lack; the copies carry the JF-363 Confirm/AutoServe band the helper lacks. Consolidating on the shared layer (extended with the band) fixes 1 and 2 at the root. CAVEAT before touching stop words: the it-IT set in KeywordMatcher lacks dei/degli/delle, and the song n-gram index is built with en-US (JF-384 asymmetry), so widening stop-word stripping must verify song-search index symmetry first (LOAD-BEARING INVARIANT: no canonical output may be a stop word in any locale).

Deliberate widening (recorded 2026-09-03, simplify ALT-1): consolidating on the shared gate makes its phonetic acceptance apply to ALL callers, including FindSong and PlayMoodMusic (pre-diff those paths already routed through the shared gate, which was equally non-phonetic), a monotone change (only boosts; length-banded phonetic collisions floor at 91, above the 85 bar), pinned by `PlayMoodMusicIntentHandlerTests.JF446_SharedGate_PhoneticAcceptance_PlaysArtist`.

JF-295 re-check (2026-09-03, review-local gate round): the tokenized guard makes the canonical case 'la ballata del genesio' GUARD-ELIGIBLE (2 content words after it-IT stripping, <= CrossMediaArtistMaxWords), so the JF-295 protection moved from the guard layer to the THRESHOLD layer. The scorer refuted the harm: the repo's own `FuzzyMatcherTests.FindBestMatch_LengthDisproportion_RejectsLambFromBallata` plus the fair length penalty keep Lamb far below 60 for the cleaned 'ballata genesio' query, so the shared gate rejects and the clean song not-found stands; pinned by `CrossMediaTypeFallbackTests.JF295_ItCanonicalCase_GuardEligibleButThresholdRefuted_StaysNotFound`. Residual risk: any future lowering of normalThreshold (DefaultThreshold or the per-user FuzzyMatchThreshold) re-opens JF-295; that change must re-visit this note and the pin test.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Artist answers with articles or 3+ raw words ('di pink floyd') no longer dead-end at the word-count guard: the gate tokenizes before counting, matching TryEntityFallbackAsync
- [ ] #2 An artist answer accepted by the fallback resolves through the phonetic ArtistSearch chain (not raw FindBestMatchWithScore at 85), or the divergence is justified in a comment with the threshold rationale
- [ ] #3 Unit test: album slot carries 'di <artist>' with musician empty, asserts the artist's music plays (not NotFoundAlbumByName)
- [ ] #4 Unit test: album slot carries a >2-word artist answer, asserts the tokenized gate outcome
- [ ] #5 The full-catalog fuzzy scan uses the cheap DTO shape (DtoOptions(false), images/userdata/current-program off) unless a regression test shows it needs more
- [ ] #6 Either PlayAlbum/PlaySong use the shared TryEntityFallbackAsync (extended with the JF-363 band) or the three-site divergence is documented with an owner note for future hardening
- [ ] #7 Any stop-word addition (dei/degli/delle) verified against song-search index symmetry before merge
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped as full consolidation (AC#6's preferred shape). Both raw gate copies (PlayAlbum ~317-390, PlaySong ~297-372) deleted; both handlers call the shared BaseHandler.TryEntityFallbackAsync, extended with the JF-363 Confirm/AutoServe band (opt-in notFoundMediaType, DisambiguationHelper consts end to end after the gate round) and phonetic acceptance (Double Metaphone overload; length-banded collisions floor at 91 above the 85 bar: 'cup' for 'Koop' plays). AC#1 tokenize-before-count via KeywordMatcher with the slot locale; AC#2 phonetic chain, not raw FindBestMatchWithScore; AC#3+4 tests ('di pink floyd' plays; >2 content words skips, plus the PlaySong mirror); AC#5 CheapDtoOptions fuzzy scan; AC#7 dei/degli/delle added with both load-bearing invariants verified and pinned. The deliberate phonetic widening for FindSong/PlayMoodMusic is recorded and pinned (JF446_SharedGate_PhoneticAcceptance_PlaysArtist). Review-gate findings applied: band wins over the word-coverage valve for band-enabled callers ('ac/dc' pin), JF-295 canonical case recorded (guard-eligible, threshold-refuted) with an it-IT pin, dropped triage logs restored. Below-cap left: the valve/band reorder's inertness for null-band callers rests on branch inspection (contrived to test). Suite 3015/0. Commit fbdfff97.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining)
- [ ] #11 Findings applied or tracked
<!-- DOD:END -->
