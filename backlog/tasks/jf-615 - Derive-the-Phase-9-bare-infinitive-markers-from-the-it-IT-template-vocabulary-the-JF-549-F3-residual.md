---
id: JF-615
title: >-
  Derive the Phase 9 bare-infinitive markers from the it-IT template vocabulary
  (the JF-549 F3 residual)
status: To Do
assignee: []
created_date: '2026-09-22 08:07'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-551 it-IT simplify round (2026-09-21): the Phase 9 WRAPPER_MARKERS hand list exhibited both drift directions in one commit - a dead added verb (leggere, zero referent samples) and a still-missing vocabulary verb (pleiare, in the template infinitive vocabulary but absent from the marker list). The JF-549 note F3 residual named exactly this years ago. The fix: check_wrapper_coverage should derive the bare-infinitive it markers from templates/it-IT.yaml's vocabulary.infinitive (entry minus the "Di " prefix, lowercased, trailing space - the convention the es/fr entries already use), keeping only the Di stems hand-listed (they encode deliberate truncations). The validator already parses every template YAML (check_template_regen_equality), so the derivation is mechanical. Apply when the wrapper-coverage triage next touches the marker table (the JF-551 en-* batch is the likely trigger).
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
