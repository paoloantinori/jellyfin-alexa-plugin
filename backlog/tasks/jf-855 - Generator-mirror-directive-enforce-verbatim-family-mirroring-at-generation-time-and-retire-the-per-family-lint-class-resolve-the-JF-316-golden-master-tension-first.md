---
id: JF-855
title: >-
  Generator mirror-directive: enforce verbatim-family mirroring at generation
  time and retire the per-family lint class; resolve the JF-316 golden-master
  tension first
status: To Do
assignee: []
created_date: '2026-10-09 23:04'
labels:
  - tooling
  - nlu
  - design
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-10 by the orchestrator from the night batch's /code-review high round (altitude finding, deliberately not applied: it is a generator redesign, tracker material).

The es-trio mirroring lint (JF-844.1) whitelists per-locale hand-mirrored rows for one intent family, but the lint's own docstrings name the root cause: verbatim-transcription templates with no shared-include mechanism (the JF-316 golden-master decision). Every future hand-mirrored cross-locale family (a mood list, a BrowseCategory carrier set) needs its own frozenset + divergence dict + lint + tests: four new maintenance surfaces per family for the same drift class.

The deeper fix: a template-side mirror/include directive in generate_interaction_model.py - generate a declared sibling from the reference locale's intent samples modulo a declared-divergence table IN THE YAML (the lint's whitelist migrates into the template as data, and mirroring is enforced at generation time, retiring the lint class wholesale). TENSION TO RESOLVE FIRST: JF-316 chose verbatim per-locale templates deliberately (the golden-master decision; each template header documents its divergences) - this task must either justify relaxing that for DECLARED-MIRROR families only (sibling templates keep their verbatim prose, one intent's samples sourced from the reference modulo the table) or record why the tension kills it. Do not start without reading the JF-316 task and the generator's template guards. Scope guard: this retires FUTURE families' cost; the existing es-trio lint stays until the directive covers it (then the JF-844.1 lint's planned expiry fires, as designed).
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
