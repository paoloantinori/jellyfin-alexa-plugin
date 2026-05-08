---
id: JF-74
title: Play recently added with time context
status: Done
assignee: []
created_date: '2026-05-04 19:01'
updated_date: '2026-05-05 19:52'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Enhance PlayLastAddedIntent to accept time context. Users should be able to say "Play something new", "Play recently added music", or with time qualifiers like "added this week", "added this month". Currently PlayLastAddedIntent exists but lacks time-scoped filtering. Jellyfin API supports date-based queries on library items.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 PlayLastAddedIntent handles 'Play something new' and 'Play recently added [music/movies]'
- [x] #2 Optional time context slot supports 'today', 'this week', 'this month' filters
- [x] #3 Jellyfin API date filters are applied when time context is provided
- [x] #4 Default behavior (no time context) remains unchanged
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Enhanced PlayLastAddedIntent with optional time_period slot (today/this_week/this_month). Added TimePeriod slot type to both en-US and it-IT interaction models. Handler resolves slot to lookback days via locale-keyed TimePeriodMap. Uses DateTime.UtcNow.Date for timezone-safe queries. All 4 acceptance criteria met. 11 unit tests + NLU fixtures added. /simplify applied: moved inline locale labels to JSON resource files, simplified searchingText branching.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->
