---
id: JF-874
title: >-
  JF-875: SkipForwardBack family missing in 11 locales (+ en-US rewind wrong-way
  latent bug)
status: To Do
assignee: []
created_date: '2026-10-12 18:37'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Cross-locale audit result from JF-869 (report-only there per dispatch): SkipForwardBackIntent exists in en-US/GB/CA/AU/IN and now it-IT, but is MISSING in de-DE, es-ES/MX/US, fr-FR/CA, hi-IN, ja-JP, nl-NL, pt-BR, ar-SA (11 locales): any "salta avanti"-class ask selects nothing there (the JF-684 no-selection class; on it-IT this was Paolo's live "salta avanti has no effect" finding). Rollout: per-locale natural skip vocabulary (verbs + direction words + time units), the SeekVocabulary.cs ER-first resolution already supports localized values over shared English ids, seek_amount digit/word-form pairing per locale (it-IT uses ItalianNumberWords per JF-814; other locales need their own word-number tables or digit-only scope), bare direction rows must be probed for NextIntent/PreviousIntent steals first (the it-IT lesson: bare avanti/indietro stole NextIntent and the rows were dropped). NLU fixtures per locale; the scratch skill 65da1ba5 is available for pre-deploy A/B.

ALSO fold in (found by the same lane): en-US "rewind {seek_amount} {seek_unit}" carries a wrong-way latent bug (empty seek_direction defaults to forward, so "rewind 30 seconds" would seek FORWARD) - the it-IT rows were reworked to avoid it; fix the en family rows the same way (direction-carrying rewind/forward rows, or a handler-side synonym map).
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
