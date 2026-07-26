---
id: JF-382
title: >-
  Refactor: eliminate PlayArtistSongsIntentHandler's duplicated 4-tier search,
  use shared ArtistSearch.SearchAsync
status: To Do
assignee: []
created_date: '2026-07-25 19:41'
labels:
  - tech-debt
  - refactor
  - artist-search
  - duplication
dependencies: []
modified_files:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/ArtistSearch.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
PlayArtistSongsIntentHandler maintains its OWN inline copy of the 4-tier artist search (in-memory Contains/Prefix/Fuzzy + DB fallback), duplicating ArtistSearch.SearchAsync (Alexa/Util/ArtistSearch.cs) which already provides the same 4-tier search WITH phonetic matching on all tiers. The shared ArtistSearch.SearchAsync is used by PlaySong, PlayAlbum, FindSong, SearchMedia, QueryArtistLibrary, AddToQueue, PlayNext, MediaInfo, and BaseHandler.TryEntityFallbackAsync.

This duplication is why JF-381 had to wire FuzzyMatchPhonetic into 5 separate inline sites in the handler instead of getting phonetic on all tiers for free. It is also a maintenance hazard: a change to the search tiers (like JF-381's phonetic floor) must be applied in two places, and they have already diverged (the inline copy has Fast/Thorough/Parallel modes the shared one may not expose).

The refactor: replace the inline 4-tier search in PlayArtistSongsIntentHandler with a call to ArtistSearch.SearchAsync, preserving the handler-specific concerns (Fast vs Thorough mode, ASR compound-word retry, fastAutoPlay disambiguation). This may require extending ArtistSearch.SearchAsync's API to expose the mode selection the handler needs.

OUT OF SCOPE for the current micro-release (JF-381 wired the phonetic overload into the 5 inline sites as a consistent stopgap). This task is the deeper cleanup.

Discovered during the JF-381 /simplify altitude review (2026-07-25).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Map the full surface of PlayArtistSongsIntentHandler's inline 4-tier search (in-memory tiers 1-4 + the DB fallback) against ArtistSearch.SearchAsync's signature/capabilities, to confirm the shared method can absorb all the handler's needs (Fast mode, Thorough mode, parallel tiers, DB fallback)
- [ ] #2 Refactor PlayArtistSongsIntentHandler to call ArtistSearch.SearchAsync instead of its inline duplicated 4-tier search, so phonetic matching is applied consistently on all tiers (today JF-381 wired FuzzyMatchPhonetic into 5 inline sites; this task eliminates the duplication that made that necessary)
- [ ] #3 Preserve the handler-specific behavior the inline search has that the shared one might not: Fast vs Thorough mode selection, ASR compound-word retry, the parallel-tier optimization, and the fastAutoPlay disambiguation path
- [ ] #4 Regression: the existing artist E2E tests (soul coughing, pink floyd, etc.) still pass; the Koop/cup phonetic match (JF-381) still resolves via the shared path
- [ ] #5 Live verify on minix: PlayArtistSongs for several artists (exact, misspelled, accent-drift) resolves identically to before the refactor
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
