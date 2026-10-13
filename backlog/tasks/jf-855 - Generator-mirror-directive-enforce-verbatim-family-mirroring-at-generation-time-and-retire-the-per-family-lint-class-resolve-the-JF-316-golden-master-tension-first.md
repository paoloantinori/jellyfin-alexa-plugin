---
id: JF-855
title: >-
  Generator mirror-directive: enforce verbatim-family mirroring at generation
  time and retire the per-family lint class; resolve the JF-316 golden-master
  tension first
status: Done
assignee: []
created_date: '2026-10-09 23:04'
updated_date: '2026-10-13 08:35'
labels:
  - tooling
  - nlu
  - design
  - 'gate:maintainer-call'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
MAINTAINER DECISION (2026-10-12 06:55, chat): DEFERRED. JF-844 resolved handler-side (not model-side), so the mirror's value is limited to retiring the es-trio lint class. The verbatim golden-master architecture (JF-316) is the maintainer's deliberate choice; the lint works and costs little. Revisit only if a second cross-locale mirror case emerges.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED 2026-10-13 per the maintainer's decision (his option "Imponi il mirroring nel generatore"; supersedes the task's 2026-10-12 DEFERRED note). Branch jf855 (8e22021b), merged to main ebe3acab, pushed. LANDED: the generator's ordered intents accept a mirror directive ({locale, exclude}) sourcing samples from the reference locale's same intent verbatim in the reference's order minus the exclude rows; generation-time enforcement errors on missing/stale/zero-sample/conflicting/cyclic mirror declarations; mirrored samples run the {ref}-vs-declared-slots guard (reviewer finding applied with a test). JF-316 TENSION RESOLVED NARROWLY: the verbatim golden-master decision is relaxed ONLY for declared-mirror families; siblings stay verbatim; the reference keeps hand-authoring. es-MX/es-US PlayEpisodeIntent converted to declared mirrors of es-ES minus the three JF-844 'que reproduzca' rows (the JF-844.1 whitelist became template data; siblings inherit future es-ES rows by deleting exclude rows). RETIRED: the es-trio lint class (ES_TRIO_CONNECTOR_TWINS, divergences table, reference constant, lint fn, phase call, test_es_trio_mirroring.py). KEPT as not expressible by mirroring: the JF-549 PLAY_EPISODE_ONESHOT_PREFIXES lint; the sibling-locale-joins-a-mirror-group decision stays a maintainer call (headers document the directive). PROOF: all 17 regenerated with ZERO model_*.json changes (es-MX/es-US cmp byte-identical); validator PASS 275 baseline (the retired lint's 3-line phase block the only stdout delta); validate_locales PASS; harness tests/scripts 74 passed; new test_mirror_directive.py covers every error shape, chains, cycles, the real-tree golden master, retired symbols' absence. Coverage note: /simplify + /code-review covered by the worker's self-review + reviewer agent (python-only, zero model delta), accepted by the orchestrator.
<!-- SECTION:FINAL_SUMMARY:END -->
