---
id: JF-878
title: >-
  JF-879: the just-below-cap library band still loses the seed tail (JF-848's
  reserved policy engages only at exact saturation) - maintainer call on band
  semantics
status: To Do
assignee: []
created_date: '2026-10-12 22:25'
labels:
  - 'gate:maintainer-call'
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review finding F1 from JF-848 (gate:maintainer-call): the reserved-slots policy engages only when the library block REACHES MaxCatalogValues (in production exactly at it, because the fetch Limit equals the cap). A library in the band just below the cap (e.g. 49999 of 50000, or a saturated fetch whose same-name dedup shrinks the block below cap post-dedup) still loses the generic word and most seeds via the JF-825 tail cut, the exact NO_SELECTION regime JF-823 fixed. This is the direct consequence of the JF-848 dispatch requirement (3): byte-identical behavior when NOT saturated. The two saturation detectors (fetch-reached-cap warning at >= cap pre-dedup vs the payload cut post-dedup) can legitimately disagree in that band; the discontinuity is documented in TruncateTo's doc. FIX if wanted: change the partition condition from libraryCount >= cap to Count > cap plus test flips, a small follow-up. Live-library evidence keeps it low priority either way (largest synced type is about 2.3 percent of cap). Paolo's call: track-and-fix later, or close as accepted discontinuity.
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
