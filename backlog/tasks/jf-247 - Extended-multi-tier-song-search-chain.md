---
id: JF-247
title: Extended multi-tier song search chain
status: To Do
assignee: []
created_date: '2026-06-03 18:13'
updated_date: '2026-07-13 20:17'
labels:
  - enhancement
  - search
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add multiple search tiers to PlaySongIntentHandler, mirroring the artist 4-tier fallback chain. Tiers would include:
1. SearchTerm full query (existing)
2. Artist-scoped NameContains with partial keywords
3. Artist-scoped keyword subset match
4. Global keyword search across all songs (no artist required)

This provides comprehensive fallback coverage but adds latency per tier. Should be an optional strategy.

**Trade-offs:** More tiers = more latency but better recall. Without artist scoping, tiers 3-4 could return false positives.

**Depends on:** jf-172 (artist-scoped keyword search - Approach A) as the foundation tier.
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
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
