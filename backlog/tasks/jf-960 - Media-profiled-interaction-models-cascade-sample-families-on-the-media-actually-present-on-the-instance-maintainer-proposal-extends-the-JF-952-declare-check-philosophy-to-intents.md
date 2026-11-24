---
id: JF-960
title: >-
  Media-profiled interaction models: cascade sample families on the media
  actually present on the instance (maintainer proposal; extends the JF-952
  declare-check philosophy to intents)
status: To Do
assignee: []
created_date: '2026-11-24 20:49'
labels:
  - nlu
  - architecture
  - design
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Maintainer proposal (2026-11-24, reacting to the JF-958 naked-genre-carrier diagnosis): make the interaction models media-aware with a cascading profile derived from the media actually present on the instance. The JF-952 declare check already applies this philosophy to CATALOG TYPES (wire only where a declared slot exists); this extends it to INTENT SAMPLE FAMILIES: at model-build time the plugin reads the library composition it already knows (LibraryFilter, the catalog census) and includes or excludes sample families by media presence. An instance with no Series drops the episode/series families (less competition, cleaner routing, less 1.5MB-cap pressure); an instance with no music drops the music intents entirely, which incidentally removes the naked genre carriers and that whole steal class. DESIGN QUESTIONS to settle before implementation: (1) SCOPE: the model is per-skill while users differ; the profile must be the per-instance UNION of linked users' libraries, or the skill-per-user question reopens (much costlier); (2) SOURCE: global library census vs per-user union vs a config override (an explicit allowlist of media types); (3) TRIGGERS: library composition changes must re-profile and rebuild (new sync moving part; interacts with the model-identity work in JF-954.7); (4) GENERATION MECHANICS: the templates are static YAML expanded at build; the profile needs a generation-time rule layer (conditional families keyed on media presence), including the finer rule the JF-958 case motivates: qualify competing naked carriers ONLY when the rival family is active (e.g. qualify Reproduce {genre} when Series is present, keep it naked on music-only instances where it steals nothing); (5) VALIDATION: the JF-954 static tools (effective model, subsumption graph) must run against the PROFILED model, and the fixtures need a media-profile annotation or the suite measures the wrong shape. This task subsumes none of JF-958 (our instance has both music and series, so the short fix there stays necessary) but is the principled long-term home for carrier-conflict policy.
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
