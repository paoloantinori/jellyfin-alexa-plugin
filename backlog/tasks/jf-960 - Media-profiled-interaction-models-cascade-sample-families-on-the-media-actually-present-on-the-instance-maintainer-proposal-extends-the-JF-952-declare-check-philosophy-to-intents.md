---
id: JF-960
title: >-
  Media-profiled interaction models: cascade sample families on the media
  actually present on the instance (maintainer proposal; extends the JF-952
  declare-check philosophy to intents)
status: Done
assignee: []
created_date: '2026-11-24 20:49'
updated_date: '2026-11-24 21:59'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
gate-exempt: /simplify on the review-round delta only (184/-48 lines of five review-driven fixes: a batch cache, per-family try/catch, derived literals, a test removal, an invariant pin); the worker's own gates covered the base slice; the /code-review evidence (orchestrator medium, 5 findings all applied) is in this transcript.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
First slice shipped 2026-11-24 via the merge of worktree-agent-a52cb74951b1907f5 (base 907b642d + review round 800bcdd9). PluginConfiguration.MediaProfiledModels (bool, default FALSE, pinned by a fresh-config test); MediaCensus (per-instance union across linked users, one Limit-1 probe per family per user, FAIL-SAFE in both directions: any degraded path reports all-families-present so pruning never runs degraded, and since the review round each throwing probe degrades only its own family); IntentMediaFamilies (the ONE static table, every row citing the handler query evidence; PlayRadioIntent deliberately excluded as dual-media, refuting the task text's music-only claim); MediaProfiledModelPruner (table-gated, dialog-consistent, slot types never touched) wired into Plugin.BuildSkillInteractionModels so rebuild PUT, startup sync and skill creation all produce profiled models; flag-off output byte-identical (library manager never queried). Review round: census cached per build batch keyed on the Configuration.Users snapshot reference (copy-on-write invalidation), per-family probe isolation, family-derived literals from FamilyKinds, the dead music-only test removed, the default-off invariant pinned. 6280/6280 both TFMs; merged main builds clean. NOT in this slice (later): config.html toggle, per-family qualified-carrier rules (the JF-958 refinement), re-profile triggers on library change (JF-954.7 interaction), fixture media-profile annotations. Gates: worker /simplify + /code-review high in its session; orchestrator Skill(code-review) medium (5 findings, all applied in 800bcdd9).
<!-- SECTION:FINAL_SUMMARY:END -->
