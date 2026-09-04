---
id: JF-491
title: Consolidate the effective fuzzy bar idiom into a named helper
status: To Do
assignee: []
created_date: '2026-09-04 20:07'
labels:
  - cleanup
  - fuzzy-matching
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The composition Math.Max(FuzzyMatcher.GetDefaultThreshold(user), bar) now appears as a repeated unnamed idiom across four sites: HandleFuzzyMiss auto-accept bar + no-qualifier bar (BaseHandler.cs ~2161/~2179, where the effective value is exactly this Max), FindSongIntentHandler singleCandidateAutoPlayThreshold (JF-487), and the CrossMediaArtistThreshold/CrossMediaAlbumThreshold doc comments (BaseHandler.cs ~83/~105) that prescribe the same idiom in prose. A FuzzyMatcher.GetEffectiveThreshold(user, bar) one-liner plus call-site/doc-comment updates would give the concept one name. Deferred from the 2026-09-04 /simplify pass on JF-487/488/489/490 (REUSE-1, low priority: one live call site, doc comments carry the rest, and the reviewer judged it naming value only). Do NOT bundle with a behavior change; pure consolidation.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
