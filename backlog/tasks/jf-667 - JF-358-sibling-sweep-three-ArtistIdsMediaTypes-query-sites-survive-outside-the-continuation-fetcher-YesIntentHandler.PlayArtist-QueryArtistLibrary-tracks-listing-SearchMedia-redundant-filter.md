---
id: JF-667
title: >-
  JF-358 sibling sweep: three ArtistIds+MediaTypes query sites survive outside
  the continuation fetcher (YesIntentHandler.PlayArtist, QueryArtistLibrary
  tracks listing, SearchMedia redundant filter)
status: To Do
assignee: []
created_date: '2026-09-29 06:45'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-666 -
    JF-666-artist-plays-stop-at-the-initial-5-track-page-the-precompute-fast-path-starves-the-continuation-and-the-fetchers-JF-358-query-shape-silently-returns-zero.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-666 code-review round (2026-09-29), all three sites verified in source.

BACKGROUND (the JF-358 rule, live-proven twice): MediaTypes=Audio does NOT constrain an ArtistIds query. CLAUDE.md's Key Gotchas (JF-358) documents it for Jellyfin 10.11.11 (returns the ENTIRE audio library); JF-666 added direct-path evidence on the live 12.1.0 box: the same artist via IncludeItemTypes=BaseItemKind.Audio returns 13 tracks, while ArtistIds+MediaTypes on the direct ILibraryManager GetItemList path returned ZERO items at StartIndex=5 (silently). The rule: every ArtistIds-filtered query must use IncludeItemTypes=BaseItemKind.Audio and must not carry a MediaTypes term. JF-666 fixed QueueContinuationFetcher.FetchArtistSongs only (the live defect); these sibling sites still carry the broken or redundant shape:

1. Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/YesIntentHandler.cs, PlayArtist (~line 401): query is ArtistIds + MediaTypes=Audio + GetItemList, NO IncludeItemTypes. Failure: confirming an artist disambiguation (or the JF-363 cross-media artist offer) with "yes" on the live 12.1.0 box can hit the zero-return shape and speak NoSongsForArtist for an artist that has tracks.

2. Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/QueryArtistLibraryIntentHandler.cs (~line 192): the tracks listing calls ListItemsByArtistAsync with includeItemTypes=null and mediaTypes={Audio} (the albums listing at ~line 170 uses the item-type path correctly). Failure: "what tracks does X have" can answer NoSongsForArtist instead of listing tracks.

3. Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/SearchMediaIntentHandler.cs (~line 515): the artist-fallback query carries BOTH MediaTypes=Audio AND IncludeItemTypes=FilterByContentAccess(_playableTypes) on an ArtistIds query. The redundant MediaTypes term is dead weight at best and the documented-broken combination at worst; drop it so the site depends only on IncludeItemTypes.

SCOPE NOTE: fix the query shapes only; do NOT restructure the handlers. Each site needs its own verification (captured-query test + the existing suite; live spot check where the path is reachable). Also note for triage: greppable via `MediaTypes = new\[\] { MediaType.Audio }` near ArtistIds; SearchService.GetArtistSongsAsync and PlayArtistSongsIntentHandler are already on the IncludeItemTypes shape (do not touch).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 YesIntentHandler.PlayArtist's artist-songs query filters via IncludeItemTypes=BaseItemKind.Audio and carries no MediaTypes term, pinned by a test that captures the InternalItemsQuery (the ProgressiveQueueTests capturing-mock pattern)
- [ ] #2 QueryArtistLibraryIntentHandler's tracks listing passes an Audio item-type filter (not a MediaType filter) into ListItemsByArtistAsync, with the captured query shape pinned by test
- [ ] #3 SearchMediaIntentHandler's artist-fallback query no longer carries MediaTypes alongside IncludeItemTypes (single filtering term), pinned by test
- [ ] #4 All existing suites green both TFMs; the three touched handlers keep their current user-facing responses byte-identical for libraries where the old shape still returned rows
- [ ] #5 Live spot check on the 12.1.0 box: confirm-artist yes-path plays tracks, 'what tracks does X have' lists them
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
