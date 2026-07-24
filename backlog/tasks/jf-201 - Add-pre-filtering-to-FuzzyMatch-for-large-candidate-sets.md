---
id: JF-201
title: Add pre-filtering to FuzzyMatch for large candidate sets
status: Done
assignee: []
created_date: '2026-05-22 05:28'
updated_date: '2026-05-22 06:14'
labels:
  - performance
  - fuzzy-matching
milestone: Performance
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/FuzzyMatcher.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

`FuzzyMatcher.FuzzyMatch()` runs O(n*m) Levenshtein comparison against the full candidate set with no pre-filtering. In libraries with 10,000+ artists, this can exceed the 6-second Alexa timeout budget, especially when called multiple times per request (artist search fallback chain).

## Implementation Plan

### Phase 1: Add length-based pre-filter

In `FuzzyMatcher.cs`, before the Levenshtein loop, filter candidates whose name length differs from the query by more than a threshold (e.g., 40% of query length). This eliminates obviously wrong candidates cheaply:

```csharp
int queryLen = normalizedQuery.Length;
int maxLenDiff = Math.Max(queryLen / 2, 3); // allow reasonable length variance
candidates = candidates.Where(c => Math.Abs(selector(c).Length - queryLen) <= maxLenDiff);
```

### Phase 2: Add first-character filter

If the query starts with an ASCII letter, skip candidates whose first character doesn't match (case-insensitive). This is a cheap O(1) check per candidate:

```csharp
if (normalizedQuery.Length > 0 && char.IsLetter(normalizedQuery[0]))
{
    char first = char.ToLowerInvariant(normalizedQuery[0]);
    candidates = candidates.Where(c =>
    {
        string name = selector(c);
        return name.Length > 0 && char.ToLowerInvariant(name[0]) == first;
    });
}
```

Note: This filter should be OPTIONAL — disabled when the caller expects ASR truncation (e.g., artist search where "led zep" should match "Led Zeppelin").

### Phase 3: Add early termination for high-confidence matches

If a candidate scores >= 90 (near-exact), skip remaining candidates and return immediately. This is already partially done in `HandleFuzzyMiss` for auto-play, but the core `FuzzyMatch` method continues scoring all candidates.

### Phase 4: Test

- Unit test: pre-filtering reduces candidate set for large inputs
- Unit test: fuzzy match results unchanged with pre-filtering enabled
- Unit test: first-character filter respects the optional flag

## Key Files
- `Jellyfin.Plugin.AlexaSkill/Alexa/FuzzyMatcher.cs`
- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs` (FuzzyMatch callers)

## Related: JF-164 (in-memory artist index — complementary, addresses same timeout from different angle)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 FuzzyMatch pre-filters candidates by length before Levenshtein scoring
- [ ] #2 FuzzyMatch optionally pre-filters by first character
- [ ] #3 FuzzyMatch terminates early on high-confidence (>=90) match
- [ ] #4 All existing FuzzyMatch tests pass unchanged
- [ ] #5 New tests verify pre-filtering behavior
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added length-based pre-filter (skip candidates differing by >max(queryLen*2,15)) and early termination on score>=90 to FindBestMatchWithScore. Added early exit on exact match to RankMatches. Before: O(n*m) Levenshtein on all candidates. After: early exit + pre-filter. Committed as a07b791.
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
