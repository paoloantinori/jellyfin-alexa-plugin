---
id: JF-69
title: Unified search across all sources
status: Done
assignee: []
created_date: '2026-05-04 18:55'
updated_date: '2026-05-06 11:12'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement a single unified search that hits all connected Jellyfin providers at once, returning combined results. Currently search is fragmented across individual search intents — the goal is one search endpoint/query that fans out to every library source the user has configured.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A single search intent queries all connected Jellyfin providers simultaneously
- [x] #2 Results from all sources are aggregated and deduplicated into a single response
- [x] #3 User experience is a single natural-language query that returns combined results across all libraries
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
