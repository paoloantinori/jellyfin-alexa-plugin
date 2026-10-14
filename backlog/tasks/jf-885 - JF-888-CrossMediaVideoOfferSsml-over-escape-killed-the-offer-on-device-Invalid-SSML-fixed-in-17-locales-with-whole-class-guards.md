---
id: JF-885
title: >-
  JF-888: CrossMediaVideoOfferSsml over-escape killed the offer on device
  (Invalid SSML) - fixed in 17 locales with whole-class guards
status: Done
assignee: []
created_date: '2026-10-14 14:36'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
EXECUTED 2026-10-14 (commit b6cb3792, deployed to minix, live-verified): the JF-865 CrossMediaVideoOfferSsml shipped an over-escaped literal backslash before the break tag's quotes (time=\"200ms\") in ALL 17 locales; the device rejected the offer response as Invalid SSML at [1,68] (the backslash position). Paolo's device round caught it ('metti il brano sunny dancer' errored); neither the plugin simulator nor simulate-skill validates SSML, so every prior self-check passed. FIX: the backslash removed in all 17 locales. GUARDS, whole-class: the dotnet theory sweeps every *Ssml key per locale (391 values) as XML inside <speak> on the ResponseStringLocaleRows roster with the key named in the failure; validate_locales.py rejects any literal backslash in an *Ssml value (the CI authoring gate, isinstance-guarded, class-neutral FAIL header). Gates: /simplify (4 agents) + /code-review high (4 applied, 1 dispositioned: embedded-staleness cross-check, mitigated by the CI gate). Suite 5958/5958 BOTH TFMs; validate_locales PASS. Deploy verified: the offer SSML on the live box is clean (no backslash). DEVICE RETEST: 'chiedi a mia collezione di mettere il brano sunny dancer' should now SPEAK the video offer and 'sì' should play it.
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
