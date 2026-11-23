---
id: JF-948
title: >-
  Song n-gram index serves stale results after a metadata edit until restart
  (reload logs the same count; issue #46 side note)
status: To Do
assignee: []
created_date: '2026-11-23 05:37'
updated_date: '2026-11-23 05:59'
labels:
  - bug
  - search
  - issue-46
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/46'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Reported live by issue #46's reporter (2026-11-22, their de-DE skill): after editing track METADATA (album/artist fields), the JF-439 inverse song fallback stopped finding the title ('keinen Kuenstler mit dem Namen die streithoernchen gefunden' = the artist-not-found path answering what should have been a song query), even though the log showed the song n-gram index reloading right after ('Song n-gram index loaded: 167 songs' at 18:45 UTC, SAME count as before the edit). A full Jellyfin restart fixed it. Hypothesis to investigate: the library-change refresh path reloads on add/remove events but serves STALE keyed entries after a metadata EDIT (the item kept its Id but its Name/artist changed, and some consumer keyed by the old shape served the stale view until process restart). Surfaces to check: DebouncedLibraryIndexService's refresh trigger set (does it include metadata-update events, or only add/remove?), SongNgramIndexService's rebuild-on-refresh (does the in-memory dictionary get REPLACED or MUTATED?), and whether the artist-scoped pre-filters (ArtistIds) read a cache that survives the reload. Reproduction: edit an album/artist tag on a track, immediately query by title via the song path, compare before/after restart.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
ROOT CAUSE (2026-11-23): DebouncedLibraryIndexService subscribed ONLY ItemAdded/ItemRemoved; a metadata edit raises ItemUpdated, so neither index service ever refreshed on re-tagging. Playback-position saves ride UserDataSaved (not ItemUpdated), so the new subscription sees metadata-shape changes, not every playback stop; the 5s debounce and ShouldRefreshOn bound it further. FIX: ItemAdded/ItemRemoved/ItemUpdated all route to OnLibraryChanged (+ dispose unsubscribe); regression test ItemUpdated_OnAudio_SchedulesARefresh in SongNgramIndexServiceTests (24/24 green). The reporter's observed reload-with-same-count was a coincidental add/remove-path refresh; with the fix the edit path itself fires deterministically. Residual to watch: if a stale-after-refresh case STILL reproduces after this fix, the snapshot swap or the artist-scoped pre-filter is the remaining surface.
<!-- SECTION:FINAL_SUMMARY:END -->
