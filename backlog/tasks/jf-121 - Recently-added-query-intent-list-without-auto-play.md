---
id: JF-121
title: '"Recently added" query intent (list without auto-play)'
status: To Do
assignee: []
created_date: '2026-05-12 04:45'
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

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
