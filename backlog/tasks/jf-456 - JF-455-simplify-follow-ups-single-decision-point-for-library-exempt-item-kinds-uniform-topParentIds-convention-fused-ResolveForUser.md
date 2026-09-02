---
id: JF-456
title: >-
  JF-455 simplify follow-ups: single decision point for library-exempt item
  kinds, uniform topParentIds convention, fused ResolveForUser
status: To Do
assignee: []
created_date: '2026-09-02 06:19'
updated_date: '2026-09-02 11:09'
labels:
  - refactor
  - library-filter
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/LibraryFilter.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/SongIndexSearch.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-455 /simplify pass (2026-09-02); three altitude findings deliberately not applied inside JF-455 because each changes behavior or shape beyond that diff. 1) Single decision point for out-of-library item kinds: BuildPlaylistPlayResponseAsync encodes "playlists are not library-scoped" twice (omitted ApplyLibraryFilter at BaseHandler ~2806 plus applyLibraryFilter:false on the SearchItemsFuzzyAsync fallback ~2816), and SearchMediaIntentHandler (its fuzzy fallback includes BaseItemKind.Playlist) still applies the filter, so which kinds are out-of-library is emergent. Suggest a kind-aware predicate consulted by LibraryFilter.ApplyLibraryFilter itself. 2) Uniform scoping convention: SearchItemsFuzzyAsync takes user+bool while the in-memory index surfaces (SongIndexSearch, SongNgramIndexService) take a caller-resolved Guid[]? topParentIds; consider making the DB surface take topParentIds too so the flag disappears. 3) Fuse GetAllowedLibraryIds + ResolveTopParentIds into LibraryFilter.ResolveForUser(user, libraryManager) and stop raw CollectionFolder ids escaping the reconciliation point; six call sites repeat the two-step (BaseHandler ~2608, PlaySongIntentHandler ~283, FindSongIntentHandler ~505, PlayArtistSongsIntentHandler ~213 and ~344, ArtistSearch ~246, DynamicEntityBuilder ~137).
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
JF-455 review F1 (P3): the album-join scopes a folderless artist to the FIRST-encountered album's library (one-shot write guard), so an artist with albums in libraries A+B is invisible to users restricted to B, and two distinct same-named artists cross-contaminate scope (bounded: the DB song queries still filter by TopParentIds, so the wrong-library case degrades to artist-found-no-songs, not a playable-content leak). Single-music-library servers unaffected. If multi-library servers report it, the map needs multi-valued scope per artist.

JF-455 review F4 (P3): JoinAlbumLibraryScopeAsync materializes ALL MusicAlbums server-wide (DtoOptions(true), no Limit) on every artist-index load/refresh when any folderless artist exists, extending the IndexWarmingGate window (SkillWarmingUp refusals after restart) proportionally on large multi-library servers. Same scale as the existing song load; revisit if warming complaints appear.

JF-455 simplify A1 (deferred): adopt Jellyfin's own BaseItem.GetTopParent() directly (item.GetTopParent()?.Id ?? item.Id, public in 10.11.8+, compile-probed) instead of the injected walk, which is now full-IsTopParent-parity but still a maintained copy. Blocker found: GetTopParent resolves parents via the STATIC BaseItem.LibraryManager, so the walk tests would need static mutation (racy across parallel xUnit collections). Adoption needs a serial test collection or a fixture owning the static. The parity walk keeps all three edges meanwhile (BasePluginFolder/Channel, livetv view, parent-is-AggregateFolder).

JF-455 simplify A3 (deferred): ResolveTopParentIds could use cf.PhysicalFolderIds (the exact property Jellyfin's own GetTopParentIdsForQuery uses) instead of the FindByPath loop over PhysicalLocationsList, removing the plugin's copy of the CF-to-physical mapping. Verify PhysicalFolderIds is populated on a cold-started server before switching (Jellyfin's own queries depend on it, so an empty value would break the DB side too). Natural home: the fused ResolveForUser item below.

JF-455 code review (2026-09-02, high effort), below-reporting-cap cleanups, verified but minor: (1) JoinAlbumLibraryScopeAsync runs the full-catalog MusicAlbum query even when byName is empty (every self-mapped artist has a blank/whitespace name): hoist an `if (byName.Count == 0) return 0;` guard above QueryAllItemsAsync (ArtistIndexService.cs ~142). (2) JoinAlbumLibraryScopeAsync has two identical foreach loops calling ScopeArtists (AlbumArtists then Artists); one loop over AlbumArtists.Concat(Artists) does the same job. (3) Artist-loop walks have no per-ParentId memoization (the album loop does): on a 1000-artist library the same 2-3 hop chain is walked ~1000 times via LibraryManager.GetItemById (LRU-cached, cold EF fallback on miss). (4) Prose rule fix: the jf-455 task file has two 'word - word' hyphen clause-breaks (lines 32 and 35 of the DESCRIPTION section) that the global manual bans; reword with semicolon or new sentence.

JF-455 code-review (high, formal) findings fixed in-commit: playlist privacy P1 (GetItemsResult skips IsVisible, other users' private playlists could surface; post-filter IsVisible added), ShouldRefreshOn now includes MusicAlbum (album-only changes kept stale scopes until restart), stale-parent albums skip without consuming the one-shot join guard, byName-empty skips the album query, walk-tail contract documented honestly (last-ancestor id). NOT fixed, tracked here: (1) SearchMedia applies the library filter to playlist-bearing queries at the PRIMARY unified site ~:122 (JF-456 item 1 previously named only the fuzzy fallback ~:140), so the #22 zero-results shape survives on the search path; (2) PlayChannelIntentHandler ~:87 primary and ~:96 fuzzy apply the filter to LiveTvChannel queries, restricted users get zero channels (channels are parented under the livetv view id, same out-of-library class as playlists); (3) DB artist tiers never set IncludeItemsByName, so folderless artists (TopParentId NULL, 1063/1149 on live) match zero rows under any TopParentIds filter when the index is cold/disabled (bounded: warm index serves steady state); (4) the library-membership predicate is copy-pasted at 4 sites (GetArtists + 3 SongNgram blocks), single-source on DebouncedLibraryIndexService; (5) latent test fragility: LibraryFilterIntegrationTests ~:142/:177, DynamicEntityBuilderTests ~:79, LibrarySyncServiceTests ~:99 assert exact TopParentIds lengths and go red if their mocks ever resolve a CollectionFolder (union semantics untested on those paths).
<!-- SECTION:NOTES:END -->

## 0.12.1 Port Review Additions (release/0.12.1, 2026-09-02)

Filed from the formal code review of the 0.12.1 port (high effort, 9 findings + below-cap cutlist); the notes above describe MAIN's architecture, so on this branch read them with these corrections: Alexa/Util/SongIndexSearch.cs and the IndexWarmingGate/SkillWarmingUp machinery do not exist at this tag (the real album-query cost here is a longer cold-index window with DB fallback), and the byName-empty hoist (cleanup 1 below) is ALREADY in the port's JoinAlbumLibraryScopeAsync.

Applied in the port, backport candidates for main: (1) per-ParentId memo caches only real scopes (sibling-stale-album poisoning, skeptic-confirmed; see the jf-455 port note); (2) the join is exception-isolated so a transient album-query failure degrades to self-mapped scopes instead of aborting the artist-index load (main self-heals via the failed-load retry, the tag does not); (3) FindSong resolves the library filter inside ONE n-gram readiness guard covering both stages.

Tracked, deliberately NOT applied in the port: (a) the debounce timer resets on every event and MusicAlbum events widen the cadence, so a sub-5s album event stream (box-set rip, watched-folder trickle) postpones the refresh and the new scope map until the stream pauses (pre-existing tag mechanism; fix is a max-pending cap on the debounce); (b) BuildPlaylistPlayResponseAsync conditionally rebuilds the QueryResult, leaving TotalRecordCount with two meanings on one variable (upstream-parity shape; unconditional rebuild is simpler); (c) joinable = byName.Sum(group => group.Count()) re-derives the named-self-mapped count through LINQ that must stay in lockstep with the lookup's Where (materializing the list once is drift-proof).

Below-cap cutlist from the same review: the album join could bound its query server-side with InternalItemsQuery.ArtistIds = self-mapped artist ids (supported on 10.11.8) instead of the full-catalog scan, a materially cheaper alternative to the F4 item; the inline playlist IsVisible predicate duplicates the visibility rule PlaylistTrackResolver.FilterAudioTracks owns; the IsVisible post-filter could live in SafeGetItemsResult (the choke point for GetItemsResult quirks) rather than one handler. Documentation finding also filed: ISongNgramIndex.topParentIds now documents the resolved-ids contract (raw GetAllowedLibraryIds output silently returns zero for restricted users, the JF-455 bug shape; the same doc gap exists on main).
