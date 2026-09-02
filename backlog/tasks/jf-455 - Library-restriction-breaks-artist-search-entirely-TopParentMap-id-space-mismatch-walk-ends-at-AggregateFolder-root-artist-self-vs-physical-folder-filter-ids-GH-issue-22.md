---
id: JF-455
title: >-
  Library restriction breaks artist search entirely: TopParentMap id-space
  mismatch (walk ends at AggregateFolder root / artist self) vs physical-folder
  filter ids (GH issue #22)
status: Done
assignee: []
created_date: '2026-09-02 05:40'
updated_date: '2026-09-02 11:11'
labels:
  - bug
  - library-filter
  - search
dependencies: []
references:
  - Alexa/Util/DebouncedLibraryIndexService.cs
  - Alexa/Util/LibraryFilter.cs
  - Alexa/ArtistIndexService.cs
  - Alexa/SongNgramIndexService.cs
  - Alexa/Handler/BaseHandler.cs
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/22'
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
GitHub issue #22 (Fishrider24, plugin 0.12.0.0, Jellyfin 10.11.10): PlayArtistSongsIntent returns zero results for every artist (log: tier=1 InMemoryContains results=0, tier=2/4 matched=False, source=InMemory, index loaded with 761 artists); PlayPlaylistIntent also empty; songs/genres/shuffle work.

ROOT CAUSE (reproduced live on minix 2026-09-02, exact log pattern, config restored after): the per-user library restriction (plugin user AllowedLibraryIds) triggers a broken in-memory filter. Chain: ArtistSearch.SearchAsync resolves LibraryFilter.GetAllowedLibraryIds -> ResolveTopParentIds (CollectionFolder GUIDs -> PHYSICAL folder GUIDs via FindByPath) -> ArtistIndexService.GetArtists(topParentIds) filters by TopParentMap[artist.Id] in topParentIds. But the map values live in a DIFFERENT id space: DebouncedLibraryIndexService.ResolveTopParentId walks the ParentId chain to its terminal ancestor, which for songs and folder-artists is the server-wide AggregateFolder root (verified: song -> album -> folder -> MusicArtist(Battisti) -> Folder(/data/media/music) -> AggregateFolder root, ParentId null), and for metadata-artists (ParentId null, path /config/data/metadata/artists/...) the walk returns the artist's OWN id. Neither ever equals the filter's physical folder ids -> zero intersection -> allArtists empty -> every tier 0 results. Songs keep working for restricted users only because the n-gram stage silently misses (same broken map; log evidence: "Built dynamic entities: 0 artists (index)") and the DB fallback answers with correct Jellyfin TopParentIds semantics (verified empirically: Jellyfin DB accepts BOTH physical folder GUIDs and CollectionFolder GUIDs for TopParentIds, 86/1149 artists matched for the Music library). The artist path never falls back to DB because the in-memory index is ready.

PLAYLIST SYMPTOM: BaseHandler.BuildPlaylistPlayResponseAsync (~:2800) applies ApplyLibraryFilter (TopParentIds) to the Playlist query. Native Jellyfin playlists live outside any media library, so any restriction excludes them all -> zero results. NOTE: on minix playlists are .m3u files inside the music tree so the filter works THERE; the reporter's playlists are presumably native. Decide: drop ApplyLibraryFilter from the Playlist query (playlists are user-scoped, query.User already gates visibility) - m3u playlists in an excluded library would then surface too, acceptable trade-off to document.

FIX DESIGN (verified premises, implementer validates details):
1. ResolveTopParentId walk (DebouncedLibraryIndexService:291): stop at the AggregateFolder boundary - when the parent is an AggregateFolder, return current.Id (the physical library folder). Fixes songs + folder-artists map values to the physical-folder id space.
2. Folderless artists: at ArtistIndexService load, additionally query MusicAlbums once and map album.ArtistIds -> resolved top parent of the album, so album-artists inherit their library (mirrors Jellyfin's own album-based scoping, verified: folderless artists match their library under DB TopParentIds).
3. Filter side: ResolveTopParentIds should emit the UNION (resolved physical ids + original CollectionFolder ids) so in-memory membership matches in both spaces and DB queries stay valid (DB accepts both, verified).
4. Playlist query: remove ApplyLibraryFilter per above.

TESTS: unit-test the walk with a mocked chain ending at an AggregateFolder; unit-test GetArtists filtering with map values in physical space vs filter in both spaces; the live repro procedure (PATCH user-skills AllowedLibraryIds=[music CF], simulator PlayArtistSongsIntent, expect play; restore) is the post-deploy verification. Workaround for affected users until release: clear the per-user Allowed Libraries restriction in the plugin config.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed, reviewed at high effort, live-verified. Root cause: with any per-user AllowedLibraryIds restriction the in-memory TopParentMap lived in a different id space than the filter (walk terminated at the AggregateFolder root or the artist's own id; filter emits physical folder ids) -> zero intersection -> every artist unfindable; the playlist query's TopParentIds excluded native playlists. Fix: walk with full IsTopParent parity (verified against v10.11.11 source + live DB); folderless artists (1063/1149 live) inherit their library via one MusicAlbum query joined by artist NAME; ResolveTopParentIds emits the physical+CF union; playlist query drops the library filter with an IsVisible post-filter. The formal code-review pass (9 confirmed findings) caught a privacy regression in my first playlist fix (GetItemsResult skips IsVisible; other users' private playlists could surface) plus join staleness (album-only changes never refreshed; stale-parent albums consumed the one-shot guard) - all fixed in commit 7ef15c3; remaining findings (SearchMedia primary site, PlayChannel, DB-tier IncludeItemsByName, predicate single-sourcing, latent test fragility) tracked on JF-456. Live verification with the restriction set: battisti tier1 results=3 (reporter's bug gone), folderless 24 grana plays (album join), song search hits the in-memory stage again, playlist found+plays (one specific m3u says empty pre-existing, same unrestricted), unrestricted regression clean, zero errors, config restored. Suite 2866/0 (8 new tests, 4 mutation-proven), Release 0 warnings. Commits 9cdcde9 + 7ef15c3. GH issue #22: diagnosis comment posted; 0.12.1 release carries the fix.
<!-- SECTION:FINAL_SUMMARY:END -->
