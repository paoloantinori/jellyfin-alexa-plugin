---
id: JF-457
title: >-
  Cold-window artist name visibility: IncludeItemsByName bypass can speak
  excluded-library artist names to restricted users (songs stay filtered)
status: To Do
assignee: []
created_date: '2026-09-02 16:49'
updated_date: '2026-09-02 17:56'
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
<!-- SECTION:NOTES:END -->
