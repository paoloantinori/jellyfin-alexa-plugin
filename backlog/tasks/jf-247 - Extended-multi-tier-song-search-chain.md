---
id: JF-247
title: Extended multi-tier song search chain
status: Done
assignee: []
created_date: '2026-06-03 18:13'
updated_date: '2026-09-15 11:08'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED superseded-by-architecture (2026-09-15). The task (2026-06-03) proposed a multi-tier song search chain mirroring the artist 4-tier fallback, depending on JF-172 and written when the plugin had 12 locales. The shipped PlaySong pipeline already implements and EXCEEDS every proposed tier: SearchTerm exact (tier 1), artist-scoped NameContains (JF-383 fallback), the O(1) n-gram index + phonetic fallback with locale-aware stop-word handling (JF-384/JF-388), the artist cascade (JF-345), and the cross-media fallbacks (JF-363/JF-463 families) - all documented in the CLAUDE.md Song Search Pipeline section. The task's own trade-off note (more tiers = more latency) was the binding constraint the shipped architecture solved with the bounded n-gram index instead of unbounded global scans. No code change; closing prevents a stale proposal from resurrecting a superseded design.
<!-- SECTION:FINAL_SUMMARY:END -->
