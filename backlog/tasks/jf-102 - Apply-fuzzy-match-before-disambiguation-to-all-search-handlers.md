---
id: JF-102
title: Apply fuzzy-match-before-disambiguation to all search handlers
status: Done
assignee: []
created_date: '2026-05-09 06:58'
updated_date: '2026-05-09 07:19'
labels: []
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs (FuzzyMatch method)
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
PlayArtistSongsIntentHandler now uses FuzzyMatch to skip disambiguation when there's a clear top match. The same pattern should be applied to 5 other handlers that have the identical "count > 1 → disambiguate" flow without a fuzzy pre-check.

The pattern to replicate is in `PlayArtistSongsIntentHandler.cs` (lines 88-98): when results.Count > 1, call `FuzzyMatch(query, results, r => r.Name)` — if a match is found, narrow to that single item; otherwise fall through to `DisambiguationHelper.AskFirstMatch`.

Handlers that need the fix:
1. PlayAlbumIntentHandler (searches albums, line ~124)
2. PlayPodcastIntentHandler (searches podcasts, line ~92)
3. PlayVideoIntentHandler (searches videos, line ~81)
4. PlayPlaylistIntentHandler (searches playlists, line ~94)
5. SearchMediaIntentHandler (unified search, line ~121)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 All 5 handlers use FuzzyMatch before entering disambiguation
- [ ] #2 Each handler uses guard-clause pattern (early return for disambiguation, continue with single match)
- [ ] #3 Existing disambiguation behavior preserved when no clear fuzzy match exists
- [ ] #4 dotnet build passes with no new errors
- [ ] #5 dotnet test passes
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Committed in 6f3de9e. All 5 search handlers (PlayAlbum, PlayPlaylist, PlayPodcast, PlayVideo, SearchMedia) now use FuzzyMatch before disambiguation. Guard-clause pattern: fuzzy match found → play directly, no match → fall through to disambiguation dialog. 3 disambiguation tests updated with non-matching test data. Build + 917 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
