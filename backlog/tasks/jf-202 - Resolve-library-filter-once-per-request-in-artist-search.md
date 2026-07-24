---
id: JF-202
title: Resolve library filter once per request in artist search
status: Done
assignee: []
created_date: '2026-05-22 05:28'
updated_date: '2026-05-22 06:03'
labels:
  - performance
  - refactor
milestone: Performance
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

`PlayArtistSongsIntentHandler` resolves the user's allowed library IDs up to 4 times per request — once per fallback tier. Each resolution calls `GetAllowedLibraryIds()` + `ApplyLibraryFilter()` which involves `ResolveTopParentIds()` and potentially file system lookups.

## Implementation Plan

### Phase 1: Compute allowedLibraryIds once in HandleAsync

At the top of `HandleAsync`, compute the library filter once:

```csharp
Guid[]? allowedLibraryIds = ApplyLibraryFilter(user);
```

Then pass `allowedLibraryIds` to all search tiers instead of re-resolving.

### Phase 2: Refactor search tiers to accept pre-computed filter

Each tier method (`SearchBySearchTerm`, `SearchByNameStartsWith`, `SearchByNameContains`) currently calls `ApplyLibraryFilter()` internally. Change them to accept `Guid[]? allowedLibraryIds` as a parameter.

### Phase 3: Test

- Verify artist search still applies correct library filtering
- Verify no behavioral change

## Key Files
- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs`

## Impact
Eliminates 3 of 4 redundant library filter resolutions per artist search request. Each resolution involves `LibraryFilter.GetAllowedLibraryIds()` + potential `ResolveTopParentIds()` calls.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 PlayArtistSongsIntentHandler resolves allowedLibraryIds exactly once
- [ ] #2 All search tiers use the pre-computed filter
- [ ] #3 Library filtering behavior unchanged
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Already implemented in current codebase. PlayArtistSongsIntentHandler resolves allowedLibraryIds and topParentIds once at method scope (lines 108-109), reuses across all 4 search tiers in both in-memory and database paths. All fallback methods (TryPrefixFallbackAsync, TryContainsFallbackAsync, TrySearchFallbackAsync) accept pre-resolved topParentIds parameter. Artist songs query also reuses the filter (line 336).
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
