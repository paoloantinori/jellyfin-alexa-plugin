---
id: JF-377
title: >-
  PlayArtist tier-4 fuzzy (InMemoryFuzzyAll) false-matches common-word artist
  names from nonsense queries
status: To Do
assignee: []
created_date: '2026-07-25 17:57'
labels:
  - bug
  - artist-search
  - fuzzy-match
  - search-quality
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/ArtistSearch.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
During Koop investigation 2026-07-25, the PlayArtist tier-4 fuzzy search (InMemoryFuzzyAll) matched a literal artist named 'artist' from the nonsense query 'zzzqqq nonexistent artist' and auto-played it. Reproduced via simulator: corr=8799e4e2, ArtistSearch tier=4 method=InMemoryFuzzyAll matched=True results=1, matched artist='artist' (id 6ed4179f-2c58-5635-bdc8-9494c581d846), played 'Track 09'.

This is a latent false-positive: the fuzzy-all tier is too permissive and can auto-play an unrelated artist when the query contains a common word that happens to be an artist name. Not the originally-reported Koop bug (the handler finds Koop correctly), but a real quality issue surfaced while debugging.

LIKELY AREA: BaseHandler.ArtistSearch tier-4 (Alexa/Util/ArtistSearch.cs) + the auto-play threshold in HandleFuzzyMiss. Compare to the word-count guards already used in the cross-media fallback (CrossMediaArtistMaxWords=2).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Reproduce: query PlayArtistSongs with a multi-word nonsense string containing a common word like 'artist' (e.g. 'zzzqqq nonexistent artist'); confirm tier-4 InMemoryFuzzyAll matches the literal artist named 'artist' and auto-plays, a false positive
- [ ] #2 Investigate the tier-4 InMemoryFuzzyAll scoring/threshold: why does a 3-word nonsense query score above the auto-play threshold against a single common word like 'artist'
- [ ] #3 Decide on a fix: raise the tier-4 threshold, add a word-coverage/length guard (similar to CrossMediaArtistMaxWords), or require a minimum match score for auto-play on the fuzzy-all tier
- [ ] #4 Regression: verify a real near-miss artist query still resolves (don't break the intended fuzzy recall), and the nonsense query no longer false-matches
- [ ] #5 Live verify on minix: the false-positive case returns a clean not-found, and a legitimate artist still plays
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
