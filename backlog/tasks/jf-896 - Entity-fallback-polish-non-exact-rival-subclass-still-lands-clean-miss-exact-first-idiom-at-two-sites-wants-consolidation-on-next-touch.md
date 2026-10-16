---
id: JF-896
title: >-
  Entity-fallback polish: non-exact-rival subclass still lands clean-miss;
  exact-first idiom at two sites wants consolidation on next touch
status: To Do
assignee: []
created_date: '2026-10-16 10:02'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-892 round (worker handback, 2026-10-16), two related polish items on the same fallback, one task: (1) BEHAVIORAL GAP: the JF-892 exact-first reorder rescues only the EXACT subclass; a coincidental-containment winner shadowing a better NON-exact rival (raw 92 / fair ~80 vs winner fair 18 in the fixture) still lands the clean not-found. The JF-776 refuse-and-continue walk is the follow-up shape; comment-documented and pinned green deliberately on the reshaped JF-894 pin (non-exact rival). (2) IDIOM CONSOLIDATION: the exact-first idiom now exists at two sites with different tails (PlayAlbumIntentHandler JF-743, CrossMediaFallback JF-892); consolidate on the next touch of either. Same class of note: only the TryEntityFallbackAsync call site reorders; the other ScoreBestWithCodes callers keep the raw order, so a future caller can reintroduce the shadow (the in-memory tier-1 corner is incidentally fixed by the shared acceptance point today).
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
