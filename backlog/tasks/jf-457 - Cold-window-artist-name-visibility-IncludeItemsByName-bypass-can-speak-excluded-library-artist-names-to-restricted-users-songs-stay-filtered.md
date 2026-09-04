---
id: JF-457
title: >-
  Cold-window artist name visibility: IncludeItemsByName bypass can speak
  excluded-library artist names to restricted users (songs stay filtered)
status: Done
assignee: []
created_date: '2026-09-02 16:49'
updated_date: '2026-09-04 21:29'
labels:
  - privacy
  - library-filter
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/ArtistSearch.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/LibraryFilter.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-456 review chain (removed-behavior audit, 2026-09-02). IncludeItemsByName=true on MusicArtist DB queries (the cold-window fix for library-restricted users finding folderless artists, whose TopParentId is NULL) makes Jellyfin's predicate 'Type==MusicArtist OR TopParentId IN ids': it matches ALL MusicArtist rows regardless of library, not only the folderless ones in the user's libraries. Songs queries keep the strict TopParentIds filter, so no playable content can leak, but a restricted profile (e.g. a kid account with AllowedLibraryIds=[kids library]) can hear an excluded library's artist NAME in a not-found message ('no songs for X') or a disambiguation prompt during the cold-index window (index null or disabled). The trade-off is documented in code comments (ArtistSearch tiers, ApplyLibraryFilter); this task tracks the optional hardening: post-filter the bypass-tier artist results by their album scope (one bounded query: the matched artist's albums' top parents must intersect the user's resolved ids) before the name is ever spoken, or gate the bypass on a config flag defaulting to the current permissive behavior. Decide with product judgment: the leak is names-only, transient (warm index serves steady state), and requires a deliberately restricted profile.
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Scope correction (formal code-review of the batch, 2026-09-02): the steady-state half of this concern (SMAPI catalog upload via LibrarySyncService and dynamic entity values via DynamicEntityBuilder) was REVERTED to the strict filter in the same batch after review confirmed those surfaces leak excluded-library names persistently (catalog) and on every new session (entities), not just in the cold window; only the transient artist-search cold-window keeps the bypass, so this task now tracks exactly the search-path trade-off as originally framed. Residual accepted gap from the revert: a restricted user's OWN folderless artists are absent from their catalog upload and dynamic entities while the artist index is cold or disabled (pre-existing behavior, unchanged).

Implementation landed (2026-09-04, post-filter approach, no config flag):
- Shared helper `ArtistSearch.FilterByAlbumScopeAsync` (+ `MaxAlbumScopeChecks=8` cap): an artist survives a bypass-tier DB result set only when a MusicAlbum query (`ArtistIds=[id]` + raw `TopParentIds` + `Limit=1`, deliberately NOT via ApplyLibraryFilter so the items-by-name bypass never fires on the verification query itself) returns a row. `ArtistIds` (not `AlbumArtistIds`) matches albums by OR containing a track by the artist, mirroring how ArtistIndexService scopes folderless artists in the warm path.
- Wired at every bypass-tier NAME surface, both search implementations: ArtistSearch.SearchAsync DB tier 1 (list-level: the whole list can be spoken, so every name is verified; an emptied tier continues the chain to tier 2), tiers 2-4 (winner-level via `KeepIfAlbumScopeAsync`: prefix candidate sets are unbounded, so only the fuzzy winner is verified, one bounded query; a failed winner empties the tier and the next tier runs), and the inline PlayArtistSongs DB tiers (Fast tier 1, Thorough tier 1 after the containment band, and the parallel tiers 2-4 via `TrySearchFallbackAsync` winner verification, all routed through the shared helper via `FilterAlbumScopeAsync`).
- Cost bounds: zero queries for unrestricted users (null scope short-circuit) and for the in-memory branch (already scoped by GetArtists(topParentIds)); at most 8 Limit=1 MusicAlbum queries per tier result set for restricted users, cold window only. Entries past the cap are DROPPED, never returned unverified (the spoken window of a disambiguation prompt is 4-8 names, JF-416).
- Verified fact via throwaway reflection probe: `InternalItemsQuery.IncludeItemsByName` is a nullable bool defaulting to NULL on 10.11.8 (a fresh query does NOT carry the bypass; only ApplyItemsByNameBypass sets it true). The probe asserted this in the unit test as `NotEqual(true, ...)` because xunit's Assert.False(null) fails.
- Audited non-leaking bypass site left raw on purpose: PlayMoodMusic's genre-artist fallback (MusicArtist + Genres query) never speaks names; a wrong-library match contributes zero tracks (its song query is strict) and costs nothing to leave as is.
- Behavior note: a bypass-found artist with NO in-scope albums now yields a clean not-found (speaking only the user's query) instead of "artist found, no songs for X" speaking the library's canonical name; that is exactly the leak this task removes.
- Tests: 4 in ArtistSearchTests (drop-excluded + keep-folderless-own + query shape, emptied-tier-1 chain continuation to tier 2, unrestricted zero-cost, cap truncation) and 3 in PlayArtistSongsIntentHandlerTests (cold-path restricted user never hears the excluded canonical name, folderless artist of the allowed library still plays, unrestricted user issues no MusicAlbum query). Full suite green at 3242 (baseline 3235 plus this batch, shared checkout).

Review + simplify dispositions (2026-09-04, orchestrator): (1) bounded-recall note (code-review 85): the cap prices the SELECTION pool, not just the spoken window; with >8 band-passing tier-1 rows and the true match past position 8 of the unordered result, a non-empty kept list short-circuits the chain before tiers 2-4 could recover it. Narrow reachability (restricted user AND cold/disabled index AND >8 rows AND true match in the tail), privacy direction correct; the MaxAlbumScopeChecks doc comment now states this honestly. (2) Batching the 8 per-artist Limit=1 queries into one ArtistIds=[ids] query was evaluated and DECLINED: the album-to-artist mapping relies on DTO artist fields whose hydration on this query path is unproven, and a wrong mapping either reintroduces the leak or over-drops, on an exceptional path the cap already bounds; latency is bounded and local. (3) The winner-level verification copy in PlayArtistSongs.TrySearchFallbackAsync was consolidated onto the now-internal ArtistSearch.KeepIfAlbumScopeAsync (simplify REUSE-1). (4) Handler tier=1 logs now fire after band+scope filtering so duration and count carry the true yield, matching SearchAsync (simplify EFFICIENCY-2).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented and deployed 2026-09-04 (commit 36304110). Album-scope post-filter on every bypass-tier NAME surface in BOTH search implementations: ArtistSearch.FilterByAlbumScopeAsync verifies each tier-1 list entry (cap 8, Limit=1 MusicAlbum query, entries past cap dropped never unverified) and KeepIfAlbumScopeAsync verifies the prefix/contains winners; the inline PlayArtistSongs Fast/Thorough/Parallel tiers route through the same shared helpers. Unrestricted users pay zero queries (live-verified on minix: 0 ArtistAlbumScope log entries). The bounded-recall trade (cap prices the selection pool) and the declined batching are documented in the code comment and these notes. 7 new tests; full suite 3243/3243; Release 0 warnings. Behavior change: a bypass-found artist with no in-scope albums now yields a clean not-found instead of speaking the excluded library's canonical name.
<!-- SECTION:FINAL_SUMMARY:END -->
