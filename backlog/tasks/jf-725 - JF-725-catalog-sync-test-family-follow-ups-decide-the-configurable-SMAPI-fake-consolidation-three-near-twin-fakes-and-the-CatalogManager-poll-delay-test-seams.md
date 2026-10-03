---
id: JF-725
title: >-
  JF-725 - catalog-sync test family follow-ups: decide the configurable SMAPI
  fake consolidation (three near-twin fakes) and the CatalogManager poll-delay
  test seams
status: Done
assignee: []
created_date: '2026-10-03 05:20'
updated_date: '2026-10-03 23:40'
labels:
  - catalog
  - testing
  - suite-velocity
dependencies:
  - JF-717
references:
  - >-
    backlog/tasks/jf-717 -
    JF-717-catalog-sync-starved-equivalence-class-locales-never-get-their-model-PUT-key-the-hash-skip-per-locale-or-wire-the-shared-catalog-into-their-model.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 same-turn from the JF-717 /simplify round (three of four agents
independently flagged the fake consolidation; the efficiency agent measured the
delay cost), carrying the findings judged real but not forceable into JF-717's
diff.

1. THE THREE NEAR-TWIN FULL-SYNC SMAPI FAKES. The Catalog sync family now carries
   three private FakeSmapiHandler classes (~80% shared shape, genuinely divergent
   knobs): LibrarySyncServiceLegIsolationTests (canary-mismatch-after-PUT,
   401-once-per-catalog, name-derived 3 catalog ids, fixed "1" versions),
   LibrarySyncServiceSeriesTests (TrackModelBuild 202+poll, 401-times,
   echo-last-PUT canary, ctor-injected catalog id, fixed "1" versions), and the
   JF-717 LibrarySyncServiceEquivalenceClassTests fake (per-catalog INCREMENTING
   versions, per-locale PUT capture, multi-locale status map). The repo's
   hoist-on-third convention (TestHelpers: CreateSyncUser, CreateLaunchBuilder,
   StubBaseItemStatics) fires on the THIRD IDENTICAL private construction; these
   are similar, not identical, so JF-717 skipped the hoist rather than build a
   6-knob god-fake mid-fix. The lockstep-edit cost is real and already paid twice
   in one diff: the "a locale absent from the status map burns the ~150s poll
   budget" fix had to be authored for the Series fake AND the new fake, each with
   its own rationale comment. The decision this task owes: either hoist ONE
   configurable fake (knobs: version counter mode, per-locale PUT capture, status
   map, 401 injection, canary mode) and fold the three suites onto it, accepting
   the knob surface, or document the per-file-fake policy for this family as
   deliberate and stop flagging it. Also mind the unrelated Func-routed
   FakeSmapiHandler in CatalogManagerPollingTests (naming collision risk).

2. THE CatalogManager POLL-PRE-DELAYS. PollSmapiOperationAsync sleeps 500ms
   before its FIRST poll and WaitForModelBuildOutcomeViaSkillStatusAsync sleeps
   500ms before its first fallback poll (CatalogManager.cs). JF-717 added the
   InterLocaleDelayMsForTest seam on LibrarySyncService (the in-class
   TypeLegEntryProbeForTest pattern) which took the 4 multi-locale pins from 32s
   to ~10s per TFM; the remaining ~10s is these two production-coded sleeps
   against fakes that answer instantly. A matching nullable-delay seam (or an
   internal delay-func) on CatalogManager would take the family to ~2s. Not
   forced into JF-717 because it touches a second production class for a
   suite-velocity win only.

Non-adopted alternative recorded for completeness (declined on merits in JF-717,
not deferred): keying the payload memo on generator identity (synonym prefix)
instead of content hash would skip the full payload rebuild+serialize+SHA256 for
every later class member. The rebuild is a 12h-cadence background cost measured
in tens of MB of transient JSON; content-hash keying is self-verifying and keeps
the dedup honest even if a future generator stops being prefix-pure. Keep the
rebuild unless a library-size measurement says otherwise.

GATE-MARKER TAIL (2026-10-04, orchestrator review of commit 02779c61, 3 findings; every
load-bearing claim mechanically verified: the hoisted JSON byte-identical 4/4 by script,
the divergence table's 7 axes real column-by-column, the seam's no-production-writer
grepped, the bucket arithmetic decomposed): F1 APPLIED (the measured-effect line's
"37 sync-family tests" relabeled to the honest grouping 26 sync + 11 polling; the
GATEWAY_ERROR pin lives in the polling bucket); F2 APPLIED (the policy markers added to
the two unmarked fakes - ModelPutFakeHandler and the PollingTests Func-routed namesake -
so the grep contract covers the whole family population); F3 APPLIED (the LegIsolation
canonical enumeration completed to all 7 axes, adding the model-GET-after-PUT and
PUT-capture rows the other two markers cite).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 The consolidation decision is made and executed: DECLINED the configurable god-fake (count and divergence table in the Final Summary); the per-file-fake policy is documented IN the family, with every fake's doc comment carrying the greppable "Per-file by policy (JF-725)" marker, the mode-knob enumeration on the LegIsolation fake, and the hoist boundary plus the absent-locale ~150s hazard on the new TestHelpers.SmapiSkillStatusJson; the policy's one genuinely-identical leaf construction (the all-SUCCEEDED per-locale-map status JSON, FOUR copies: the three full-sync fakes plus CatalogManagerTests' ModelPutFakeHandler) IS hoisted there
- [x] #2 If adopted, the fake consolidation keeps every existing pin green on both TFMs with no assertion weakened: the leaf hoist changed zero assertions; full suite 5078/5078 net9.0 AND net10.0 (baseline 5078), the five delay-paying suites 79/79 on both TFMs; the code-review round verified the helper's output is byte-identical to all four replaced inline constructions
- [x] #3 The CatalogManager delay seam decision lands: ADOPTED as internal int? PollDelayMsForTest (the InterLocaleDelayMsForTest pattern, JF-717) + private int PollDelayMs computed property, read at the updateRequest poll loop seed, the skill-status fallback tracker's pre-delay and loop seed, the settle wait's loop seed (threaded as an optional initialDelayMs parameter so the static method's out-of-class caller SmapiManagement.GetLiveModelJsonAsync keeps the production default), and the transient-fetch retry backoff; wired to 0 in the five delay-paying suites (LegIsolation, Series, EquivalenceClass ctors; CatalogManagerTests.CreatePollingManager; CatalogManagerPollingTests.CreateManager). Measured: 41.3s + 5.2s per TFM before, 3s after (79 tests)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle: worker commit 02779c61 + gate-marker tail ef33291f, merged as a553e7af. The catalog test-family decisions: the god-fake consolidation DECLINED with the 7-axis divergence table (the fakes encode genuinely different pin scenarios; per-file the documented family policy with greppable markers covering the whole population after the tail), the one identical leaf construction hoisted (TestHelpers.SmapiSkillStatusJson, byte-identical 4/4 by script, the absent-locale hazard documented once), and the PollDelayMsForTest nullable seam adopted (the JF-717 pattern; no production writer, the out-of-class caller keeps production pacing) taking the delay-paying suites from 46.5s to 3s per TFM - the full suite now ~1m10s per leg. Worker gates green (simplify 3 applied; code-review high 4 doc-level applied, nothing cut, nothing out-of-scope); the orchestrator gate-marker mechanically verified every load-bearing claim (the byte-identity by script, the divergence table column-by-column, the seam's no-production-writer, the bucket arithmetic decomposed) with 3 findings all applied in the tail (the honest bucket decomposition, the two missing policy markers, the completed 7-axis canonical enumeration). Suites: worker 5078/5078 both TFMs (no count change; the effect is velocity), orchestrator independent 5078/5078 at 1m07s/1m12s, merged-tree 5078/5078 both TFMs exit 0 on both split legs. Production file touched with the seam null (behavior identical): deployed to keep the box at main, verified (md5 efab714e, config intact, smoke green).
<!-- SECTION:FINAL_SUMMARY:END -->
