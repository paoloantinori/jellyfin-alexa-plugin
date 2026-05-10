---
id: JF-114
title: Apply PlayCount+Rating sort to PlayArtistSongsIntent selection
status: Done
assignee: []
created_date: '2026-05-09 20:41'
updated_date: '2026-05-09 21:10'
labels:
  - enhancement
  - playback
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
PlayArtistSongsIntentHandler currently has no OrderBy on its song query (line 105-112), so it always plays the first track in Jellyfin's default sort order (album/disc/track).

Apply the same ranking logic already used in QueryArtistLibraryIntentHandler (line 149):
```csharp
OrderBy = new[] { 
    (ItemSortBy.PlayCount, SortOrder.Descending), 
    (ItemSortBy.CommunityRating, SortOrder.Descending), 
    (ItemSortBy.SortName, SortOrder.Ascending) 
},
```

This sorts songs by: most-played first, then highest-rated, then alphabetical. The utterance "metti una canzone dei soul coughing" would then play the most popular song rather than the first track of the first album.

The change is a single line addition to the InternalItemsQuery in PlayArtistSongsIntentHandler.cs.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added PlayCount+CommunityRating+SortName sort order to PlayArtistSongsIntentHandler song query.

Extracted the shared `PopularitySort` constant into `BaseHandler` to eliminate duplication with `QueryArtistLibraryIntentHandler`. Removed now-unnecessary `using Jellyfin.Database.Implementations.Enums` from PlayArtistSongsIntentHandler.

Build: 0 errors. Tests: 983 passed, 0 failed.
<!-- SECTION:FINAL_SUMMARY:END -->
