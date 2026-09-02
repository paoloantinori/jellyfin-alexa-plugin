---
id: JF-456
title: >-
  JF-455 simplify follow-ups: single decision point for library-exempt item
  kinds, uniform topParentIds convention, fused ResolveForUser
status: To Do
assignee: []
created_date: '2026-09-02 06:19'
updated_date: '2026-09-02 08:39'
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
<!-- SECTION:NOTES:END -->
