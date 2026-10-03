---
id: JF-725
title: >-
  JF-725 - catalog-sync test family follow-ups: decide the configurable SMAPI fake
  consolidation (three near-twin fakes) and the CatalogManager poll-delay test seams
status: To Do
assignee: []
created_date: '2026-10-03 05:20'
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
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] The consolidation decision is made and executed (hoisted configurable fake with the three suites delegating, or a documented per-file-fake policy in the test family)
- [ ] If adopted, the fake consolidation keeps every existing pin green on both TFMs with no assertion weakened
- [ ] The CatalogManager delay seam decision lands the same way (seam adopted with the family using it, or declined with a reason recorded here)
<!-- DOD:END -->
