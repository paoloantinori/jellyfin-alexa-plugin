---
id: JF-266
title: >-
  BrowseLibrary mostra libri: first item is root folder "Audiobooks" not actual
  content
status: Done
assignee: []
created_date: '2026-06-06 13:43'
updated_date: '2026-06-06 15:16'
labels:
  - bug
  - browse
  - library
  - it-IT
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When the user says "mostra libri" (BrowseLibraryIntent with browse_category=libri), the response list includes "Audiobooks" as the first item. This appears to be the root folder name in Jellyfin rather than an actual book/audiobook item.

**Reproduction:**
1. "Alexa, chiedi a mia collezione di mostrare i libri"
2. Response starts with "1. Audiobooks" (or similar generic name)
3. This is likely the parent folder, not actual content

**Expected:** Browse results should skip root/library containers and show actual items (books, audiobooks), or at minimum exclude generic folder names.

**Investigation needed:**
1. Check BrowseLibraryIntentHandler — does it query with the correct `IncludeItemTypes` filter for books?
2. Check if the query returns parent folders alongside child items
3. Check if "Audiobooks" is a Jellyfin library root that should be filtered out
4. Verify the handler distinguishes between library folders and actual media items
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed BrowseLibrary "mostra libri" showing "Audiobooks" root folder as first item.

**Root cause:** AudioBook deduplication resolves parent folders to get book names. In flat library structures where tracks sit directly under the library root, the parent resolution returned the "Audiobooks" CollectionFolder as a result.

**Fix:** Filter out CollectionFolder and AggregateFolder from parent resolution results in QueryItems method. These are organizational containers, not actual books. Added debug logging when folders are filtered.

**Tests:** 2 new tests (CollectionFolder filtering, AggregateFolder filtering). Updated existing dedup test to use Folder instead of CollectionFolder as parent. All 2234 tests pass.

**Commit:** b86e0e1
<!-- SECTION:FINAL_SUMMARY:END -->
