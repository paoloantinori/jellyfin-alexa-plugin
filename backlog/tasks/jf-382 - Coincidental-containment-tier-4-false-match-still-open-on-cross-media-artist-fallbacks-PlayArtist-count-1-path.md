---
id: JF-382
title: >-
  Coincidental-containment tier-4 false-match still open on cross-media artist
  fallbacks + PlayArtist count>1 path
status: To Do
assignee: []
created_date: '2026-07-27 04:18'
labels:
  - bug
  - artist-search
  - fuzzy-match
  - search-quality
  - tech-debt
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spawned from JF-377 /code-review high (altitude agent findings Q1 + Q4).

JF-377's disambiguation-downgrade fix only covers the PlayArtistSongsIntentHandler artists.Count==1 path (the live repro). The same coincidental-containment false-positive shape (a short common-word name winning via PartialRatio's ContainmentScore=90 shortcut against a longer nonsense/carrier query) still ships through:

1. Q4 - PlayArtistSongs count>1 paths: the Fast-mode fastAutoPlay branch (picks one via FuzzyMatchPhonetic, auto-plays) and the Thorough-mode count>1 branch (HandleFuzzyMiss auto-accepts at score >= ContainmentScore). A coincidental-containment candidate at 90 slips through both.

2. Q1 - 12 other ArtistSearch.SearchAsync callers re-score via FuzzyMatcher.FindBestMatchWithScore (which returns ContainmentScore for the shape): BaseHandler.TryEntityFallbackAsync (cross-media fallback for PlaySong/PlayAlbum/PlayMoodMusic greedy-slot misroutes), PlaySongIntentHandler, PlayAlbumIntentHandler, FindSongIntentHandler, PlayNext/AddToQueue/QueryArtistLibrary/MediaInfo/SearchMedia. These do NOT consult IsCoincidentalContainmentMatch. Mitigating factor: the cross-media paths gate on CrossMediaArtistMaxWords=2, so the surface is narrower, but a 2-word query like "xyzzy artist" still hits it.

LIKELY FIX (research-grounded, from claudedocs/research_jf377_discriminator_2026-07-26.md): the discriminator problem is string-indistinguishable, so the fix is the same downgrade-to-disambiguation pattern, not a reject. Options: (a) thread a MatchShape flag (Genuine/CoincidentalContainment) through ArtistSearch.SearchAsync's return type so all 13 callers can downgrade consistently; (b) apply IsCoincidentalContainmentMatch at each caller's single-best-match point before auto-play. Lower priority than JF-377 (the direct PlayArtist path was the reported repro); file when a user reports the cross-media/count>1 variant.

Predicate ArtistSearch.IsCoincidentalContainmentMatch (stop-word-aware, locale param) is the reusable trigger. See JF-377 for the full design + the 3-reject-attempt history.
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
