---
id: JF-877
title: >-
  JF-878: SkipForwardBack wrapper-tail twins for fr/es/nl, gated on those
  locales gaining wrapper compositions
status: To Do
assignee: []
created_date: '2026-10-12 21:48'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From JF-875's execution (worker draft, renumbered to avoid the JF-876 collision): SkipForwardBack one-shot wrapper-tail twins for the fr/es/nl payload shapes. GATED: no working one-shot wrapper compositions exist for those locales today (their wrapper payloads differ from it-IT's Di-form tail), so the twins cannot be probed until a wrapper composition lands; the en family needs none (its wrapper payload IS the bare imperative, already covered). Revisit when any fr/es/nl wrapper family gets built (the JF-844-class discipline then applies: bare-infinitive literal rows verified probe-first).
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
