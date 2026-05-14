---
id: JF-121
title: '"Recently added" query intent (list without auto-play)'
status: Done
assignee: []
created_date: '2026-05-12 04:45'
updated_date: '2026-05-12 11:57'
labels:
  - enhancement
  - discovery
  - ux
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add a query-style "what's new" / "what was recently added?" intent that reads back recently added items without auto-playing them. We have PlayLastAddedIntent which auto-plays, but lack a discovery/query mode that lists what's new and lets the user choose.

Inspired by Plex official skill's "what's on deck" and JellyMusic's query capabilities. The pattern is: ask → list results → user picks one → play.

Implementation: New intent handler (e.g., QueryRecentlyAddedIntent) that fetches recent items via Jellyfin API `/Items` with sort by DateCreated descending, then speaks the list back with numbered choices.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 'What's new' or 'What was recently added?' voice command returns a spoken summary of recently added items
- [ ] #2 Does NOT auto-play — reads back results with option to play a specific item
- [ ] #3 Covers audio and video media types
- [ ] #4 Localized in all 12 locales
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
New QueryRecentlyAddedIntent handler fetches recent items (Audio, Movie, Episode, MusicAlbum) sorted by DateCreated descending, reads back a numbered spoken list. Does NOT auto-play. Added intent name constant, 5 response strings in all 12 locales, interaction model entries in all 12 locales, and 10 unit tests. Build clean, 1056 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
- [ ] #2 dotnet build passes with 0 errors
- [ ] #3 dotnet test passes
- [ ] #4 No new compiler warnings introduced
- [ ] #5 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #6 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #7 NLU test fixtures updated if interaction model changed
- [ ] #8 E2E test added for new intent or handler logic
- [ ] #9 Locale response strings added to all 12 locales
<!-- DOD:END -->
