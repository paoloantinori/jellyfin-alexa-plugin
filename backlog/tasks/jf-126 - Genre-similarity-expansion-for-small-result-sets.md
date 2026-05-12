---
id: JF-126
title: Genre similarity expansion for small result sets
status: To Do
assignee: []
created_date: '2026-05-12 04:45'
labels:
  - enhancement
  - discovery
  - search
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When a genre search yields few results, automatically expand to include similar/related genres. This helps users with niche genres or small libraries get meaningful playback results.

Inspired by JellyMusic's response variants for "similar genres": `GENRE_PLAYING_SIMULAR`, `GENRE_SHUFFLE_SIMULAR`, `GENRE_QUEUED_SIMULAR`.

Implementation: In PlayByGenreIntent, if result count is below a threshold, query for related genres (could use Jellyfin's genre relationships, last.fm API, or a local mapping). Merge results and inform the user.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 When exact genre match yields few results, skill expands to include related/similar genres
- [ ] #2 Genre similarity can be based on Jellyfin genre tags or a configurable mapping
- [ ] #3 User is informed when similar genres are included ('playing rock and similar genres')
- [ ] #4 Localized in all 12 locales
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
