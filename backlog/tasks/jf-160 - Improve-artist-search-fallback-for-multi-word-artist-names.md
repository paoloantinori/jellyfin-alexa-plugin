---
id: JF-160
title: Improve artist search fallback for multi-word artist names
status: Done
assignee: []
created_date: '2026-05-16 14:31'
updated_date: '2026-05-16 14:50'
labels:
  - bug
  - search
dependencies: []
modified_files:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**User feedback**: "My kids listen to Kidz Bop and I cannot get the player to play that one, it always says there are no songs for Kidz Bop."

**Root cause**: `PlayArtistSongsIntentHandler` (lines 96-133) first tries Jellyfin's `SearchTerm` for an exact match, then falls back to a prefix search using only the **first word** (`NameStartsWith = firstWord`). If the artist in Jellyfin is stored as "Kidz Bop Kids" or similar variant, the `SearchTerm` may not match, and the single-word prefix "Kidz" might return too many/no results, or the fuzzy match on those results falls below threshold.

**Investigation needed**:
1. Log the fuzzy scores when the prefix fallback fires to understand the gap
2. Check if Jellyfin's `SearchTerm` handles partial artist name matches

**Proposed fix**: Add a second fallback that uses `NameStartsWith` with the full query string (not just the first word), or use `NameContains` as an additional search strategy before giving up.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Multi-word artist names like 'Kidz Bop' are found even when library has 'Kidz Bop Kids'
- [ ] #2 Add logging for fuzzy fallback scores to aid future debugging
- [ ] #3 Existing artist search tests pass
- [ ] #4 New test case for multi-word artist fallback
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Implementation Plan — JF-160

### Problem
Artist search fallback at PlayArtistSongsIntentHandler.cs:112-134 only uses the first word of the query as prefix. "Kidz Bop" → prefix "Kidz" may not return the right artist if stored as "Kidz Bop Kids".

### Approach
Add a second fallback tier: after the first-word prefix fails, try `NameStartsWith` with the full query string. Also add structured logging for fuzzy scores so we can debug future cases.

### Steps

1. **Add full-query prefix fallback** — In `PlayArtistSongsIntentHandler.cs`, after the existing first-word prefix block (lines 112-134) and before the `artists.Count == 0` check (line 136), add a second fallback:
   ```
   if (artists.Count == 0 && firstWord != musician)
   {
       // Try full query as prefix (e.g. "Kidz Bop" → NameStartsWith "Kidz Bop")
       var fullPrefixQuery = new InternalItemsQuery()
       {
           Recursive = true,
           NameStartsWith = musician,
           IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
           DtoOptions = new DtoOptions(true)
       };
       ApplyLibraryFilter(fullPrefixQuery, user, _libraryManager);
       
       var fullPrefixArtists = await RetryAsync(
           () => _libraryManager.GetItemList(fullPrefixQuery),
           "GetArtistsFullPrefix",
           cancellationToken).ConfigureAwait(false);
       
       BaseItem? fuzzy = FuzzyMatch(musician, fullPrefixArtists, a => a.Name, user);
       if (fuzzy != null)
       {
           _logger.LogInformation("Full-prefix fallback matched '{Name}' for query '{Query}'", fuzzy.Name, musician);
           artists = new List<BaseItem> { fuzzy };
       }
   }
   ```

2. **Add logging for existing first-word fallback** — At line 129-133, add:
   ```
   _logger.LogInformation("First-word prefix fallback for '{Query}': {Count} candidates, best match '{Name}' score {Score}",
       musician, prefixArtists.Count, fuzzy?.Name, /* score */);
   ```

3. **Add test** — In the handler tests, add a test case for multi-word artist name fallback with a mock `_libraryManager` that returns empty for `SearchTerm` but matches for `NameStartsWith` with full query.

4. **Verify** — `dotnet test`

### Files
- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs` (lines 112-138)
- `Jellyfin.Plugin.AlexaSkill.Tests/` (new or existing handler test file)
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added full-query prefix fallback in PlayArtistSongsIntentHandler.cs for multi-word artist names. When SearchTerm and first-word prefix both fail, a second fallback now tries NameStartsWith with the full query string (e.g. "Kidz Bop" instead of "Kidz"). Also added structured logging for fuzzy match scores. New test PlayArtistSongs_FullPrefixFallback_MatchesMultiWordArtist passes. 1467 tests pass, 0 new failures.
<!-- SECTION:FINAL_SUMMARY:END -->

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
