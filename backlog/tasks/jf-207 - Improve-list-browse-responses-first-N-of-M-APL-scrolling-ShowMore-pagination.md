---
id: JF-207
title: >-
  Improve list/browse responses: "first N of M", APL scrolling, ShowMore
  pagination
status: Done
assignee: []
created_date: '2026-05-22 21:02'
updated_date: '2026-05-25 11:32'
labels:
  - feature
  - apl
  - ux
  - pagination
  - locale
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When browsing content (books, albums, in-progress), the voice says "I found 5 items" without clarifying these are just the first batch. APL only shows 5 items despite native scroll support. No way to see more.

## Phase 1 — Quick Wins
- Add MaxListDisplayItems (15), MaxInProgressDisplayItems (10), MaxQueueDisplayItems (10) to PluginConfiguration
- Fix 5 list handlers: split voice (5 spoken) vs APL display (15) limits
- Add partial locale keys (BrowseResultsPartial, InProgressListPartial, QueueListPartial) to all 17 locales
- Use ResponseBuilder.Ask() when more pages exist

## Phase 2 — ShowMore Pagination
- Create ListPaginationHelper for session-based state
- Create ShowMoreIntentHandler
- Add ShowMoreIntent to all 17 interaction models
- Add pagination delegation to YesIntentHandler

## Verification
- dotnet build 0 errors
- dotnet test all pass
- validate_locales.py clean
- validate_interaction_models.py clean
- Tests for new handlers and pagination logic
- /simplify review before closing
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Voice says 'showing first N of M' when list is truncated
- [ ] #2 APL carousel/list shows 15 scrollable items instead of 5
- [ ] #3 ShowMoreIntent handler returns next page from session state
- [ ] #4 Yes intent delegates to show-more when pagination state exists
- [ ] #5 All 17 locale files have new keys with translations
- [ ] #6 All 17 interaction models include ShowMoreIntent
- [ ] #7 dotnet test passes with new tests for pagination
- [ ] #8 /simplify invoked and issues fixed
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Completed: list/browse responses now show "first N of M" when truncated, APL displays up to 15 scrollable items, ShowMoreIntent handler pages through results, YesIntent delegates to pagination when state exists. All 17 locales and interaction models updated. /simplify review fixed EscapeXml consistency gap and voice separator mismatch. 1879 tests pass.
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
