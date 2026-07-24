---
id: JF-363
title: >-
  Offer cross-media artist matches as a disambiguation prompt when song/album
  not found (richer not-found experience)
status: To Do
assignee: []
created_date: '2026-07-23 20:56'
labels:
  - search
  - ux
  - disambiguation
  - artist
  - i18n
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
FEATURE: when a PlaySong/PlayAlbum request finds no exact match but the cross-media artist fallback finds a confident-enough artist (score in 60..85, above the normal threshold but below the strict CrossMediaArtistThreshold=85 gate), OFFER it via the existing disambiguation flow instead of silently failing with "non ho trovato nessuna canzone/album". Turns the conservative cross-media rejection (gaps A/B from JF-362 analysis) into a helpful confirmation prompt with zero risk of a wrong silent substitution.

USER EXPERIENCE:
- Today: "riproduci soul coffin" (routed as song, no song found) -> "Spiacente, non ho trovato nessuna canzone chiamata soul coffin." (dead end, because the artist fallback scored 63 < 85 strict gate and was rejected).
- Richer: "Non ho trovato un brano 'soul coffin'. Forse cercavi un artista? Ho trovato Soul Coughing. Vuoi riprodurre la sua musica?" -> user "Sì" -> plays Soul Coughing (with the existing FoundArtistInstead announcement path).
- Multi-candidate variant: if the search returned several plausible artists, "Ho trovato Soul Coughing, e anche Soul Train. Quale vuoi?" -> "Il primo" -> plays.

WHY THIS IS SAFE (resolves the false-positive concern that kept the gate strict):
The 85 gate exists because silently substituting a wrong artist is worse than a clean not-found. OFFERING the match (user confirms before anything plays) carries no false-positive risk: nothing wrong ever plays without the user's yes. So the offer can use the looser normal threshold (60) that the artist route uses, while the silent-substitute path stays strict.

EXISTING MACHINERY TO REUSE (verified by code reading 2026-07-23):
- BaseHandler.HandleFuzzyMiss already has a Confirm mode ("Did you mean X?") for borderline matches, with session state (disambig_matches/disambig_index/disambig_type) and yes/no handling. See BaseHandler.cs ~line 1540+.
- DisambiguationHelper (Alexa/Handler/DisambiguationHelper.cs): AskFirstMatch holds up to 3 MatchInfo candidates and walks them; MediaTypes include song/album/artist/video/playlist/podcast.
- YesIntentHandler already confirms a disambiguation match and routes by media type (PlayBook routing for audiobooks is the JF-361 reference pattern).
- The cross-media fallback in PlaySongIntentHandler (lines ~217-265) and PlayAlbumIntentHandler (lines ~186-210) already compute the candidate + score; today they reject at <85. The change: instead of rejecting, when score is in [60,85) AND the catalog returned ER_SUCCESS_NO_MATCH, hand the candidate to the disambiguation offer.

DESIGN NOTES / SCOPE:
- Trigger condition: catalog ER_SUCCESS_NO_MATCH (the name isn't a known song/album) AND artist candidate score in [GetDefaultThreshold(user)=60, CrossMediaArtistThreshold=85). This is exactly the band that currently fails silently.
- Keep the 2-word cap (CrossMediaArtistMaxWords=2) for the OFFER too? Debatable: the cap prevents long-query wrong-artist offers. Probably keep it, but the "i sol coffin" 3-word case (Gap C) could be reconsidered since an offer is non-destructive.
- Per-user toggle: there's already FuzzyMatchBehavior (AutoPlay/Confirm). The offer is essentially a richer Confirm; respect the existing per-user/global setting rather than forcing it. If a user is AutoPlay mode, maybe still offer (not auto-substitute) for cross-media since it's a different media type than asked.
- Prompts need new locale strings in all 17 locales (it-IT via YAML template, others via JSON): a "not found as song/album, but found artist X, want it?" prompt + reprompt.
- The announcement on confirm reuses FoundArtistInstead (already exists).

ACCEPTANCE:
- PlaySong "soul coffin" (score 63, catalog NO_MATCH) -> offers Soul Coughing -> "sì" plays it.
- PlaySong with a name that has NO plausible artist (score <60) -> still clean "not found" (no spurious offer).
- PlayAlbum equivalent.
- Existing silent-substitute behavior at score >=85 unchanged (still auto-plays with announcement).
- Unit tests: a PlaySong test where the cross-media candidate scores in [60,85) asserts a disambiguation Ask response (not a Tell, not an auto-play). Plus the >=85 auto-play regression and <60 not-found regression.

OUT OF SCOPE: catalog synonym flakiness for multi-word names (Amazon-side, not plugin-fixable); Gap D (heavy accents scoring <60, nothing to offer).

Related: JF-362 (the coverage-synonym work that surfaced these gaps), JF-337 (cross-media fallback architecture).
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
