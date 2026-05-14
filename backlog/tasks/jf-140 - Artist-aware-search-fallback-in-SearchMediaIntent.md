---
id: JF-140
title: Artist-aware search fallback in SearchMediaIntent
status: In Progress
assignee: []
created_date: '2026-05-14 07:09'
updated_date: '2026-05-14 07:12'
labels:
  - search
  - bug
  - artist
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

When a user asks "what songs do we have by Soul Coughing", `SearchMediaIntent` uses Jellyfin's `SearchTerm` query which only matches item **titles**. This misses artist-name queries entirely — "soul coughing" matches 1 album with that name in the title, but not the 87 songs where Soul Coughing is the artist.

## Current Behavior

```
SearchTerm = "soul coughing" → matches only "Lust in Phaze: The Best of Soul Coughing" (album)
```

## Expected Behavior

When `SearchTerm` returns few or no results, fall back to an artist-based query using Jellyfin's `ArtistIds` filter:

```
1. SearchTerm = "soul coughing" → 1 result (album)
2. If results are sparse → look up artist: GET /Artists?SearchTerm=soul+coughing → artist ID
3. Query by artist: GET /Items?ArtistIds={id}&IncludeItemTypes=Audio → 87 songs
4. Return disambiguation list of top matches
```

## Affected Code

- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/SearchMediaIntentHandler.cs` — lines 95-117 (search query construction)
- Uses `InternalItemsQuery.SearchTerm` only — needs artist fallback logic

## Approach

After the initial `SearchTerm` search, if results are below a threshold (e.g., `<= 3` and none are a strong fuzzy match to the query), perform an artist lookup via `ILibraryManager` and re-query with the artist ID. This preserves the existing search behavior for title-based queries while adding artist-awareness.

## Related

- The simulator test confirmed: `Search for 'soul coughing' returned 1 deduplicated results` when 87 songs exist
- Diagnostic logging was added in this session to trace search flow
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
