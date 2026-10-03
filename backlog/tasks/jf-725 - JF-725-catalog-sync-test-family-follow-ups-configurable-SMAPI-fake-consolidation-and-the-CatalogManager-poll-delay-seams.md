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
- [x] The consolidation decision is made and executed: DECLINED the configurable god-fake (count and divergence table in the Final Summary); the per-file-fake policy is documented IN the family, with every fake's doc comment carrying the greppable "Per-file by policy (JF-725)" marker, the mode-knob enumeration on the LegIsolation fake, and the hoist boundary plus the absent-locale ~150s hazard on the new TestHelpers.SmapiSkillStatusJson; the policy's one genuinely-identical leaf construction (the all-SUCCEEDED per-locale-map status JSON, FOUR copies: the three full-sync fakes plus CatalogManagerTests' ModelPutFakeHandler) IS hoisted there
- [x] If adopted, the fake consolidation keeps every existing pin green on both TFMs with no assertion weakened: the leaf hoist changed zero assertions; full suite 5078/5078 net9.0 AND net10.0 (baseline 5078), the five delay-paying suites 79/79 on both TFMs; the code-review round verified the helper's output is byte-identical to all four replaced inline constructions
- [x] The CatalogManager delay seam decision lands: ADOPTED as internal int? PollDelayMsForTest (the InterLocaleDelayMsForTest pattern, JF-717) + private int PollDelayMs computed property, read at the updateRequest poll loop seed, the skill-status fallback tracker's pre-delay and loop seed, the settle wait's loop seed (threaded as an optional initialDelayMs parameter so the static method's out-of-class caller SmapiManagement.GetLiveModelJsonAsync keeps the production default), and the transient-fetch retry backoff; wired to 0 in the five delay-paying suites (LegIsolation, Series, EquivalenceClass ctors; CatalogManagerTests.CreatePollingManager; CatalogManagerPollingTests.CreateManager). Measured: 41.3s + 5.2s per TFM before, 3s after (79 tests)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Both JF-717 simplify-round follow-ups decided on measured evidence; worktree
commit follows this summary.

DECISION 1, the three-fake consolidation: DECLINED the configurable shared
fake; per-file fakes are now the DOCUMENTED policy for this family. The count
today: THREE near-twin full-sync fakes (LibrarySyncServiceLegIsolationTests
~120 lines, LibrarySyncServiceSeriesTests ~129, the JF-717
LibrarySyncServiceEquivalenceClassTests ~113) plus ONE unrelated Func-routed
namesake in CatalogManagerPollingTests (9 lines; no collision: the three are
private nested classes in a different namespace). The divergence is in MODE
knobs, not values: catalog-id derivation (name-derived 3-type ids | ctor
injected | fixed artist id), version numbering (fixed "1" | fixed "1" |
per-catalog incrementing), 401 injection (once per catalog id on the version
upload | N times on the version upload | once on the MODEL PUT), model PUT
response (200 | 200 or 202+pollable Location per TrackModelBuild | 200),
model GET after PUT (static seeds + canary-mismatch variant | echo-last-PUT |
static seed), PUT capture (last body | last body | per-locale dictionary),
status map (static it-IT | static it/es-MX/es-US | mutable 4-locale set). A
shared fake needs ~8 mode knobs whose combinations only 3 of N are ever
exercised; each suite would lose the in-place "minimum surface" doc its fake
carries, and a knob-default change would silently reshape distant suites'
backends. Weighed against that, the lockstep-edit cost (realized once, the
absent-locale status-map fix authored twice in JF-717) is addressed at its
actual altitude: the ONE leaf construction that lockstep-edited (the
all-SUCCEEDED status JSON, four copies) is hoisted to
TestHelpers.SmapiSkillStatusJson with the ~150s absent-locale hazard
documented once; per the simplify round, the pure-boilerplate Json() response
helpers (5 trivial copies) stay per-file, hoistable opportunistically on the
next family edit.

DECISION 2, the poll-delay seams: ADOPTED the nullable-int instance seam over
the alternatives (internal delay-func: grants power no test uses; TimeProvider:
a DI-surface change for a test-only need with zero repo precedent; static
seam: races under xUnit class parallelization). internal int?
PollDelayMsForTest + a private int PollDelayMs computed property single-home
the coalesce (the /simplify round's F1); four poll sites read the property,
and the transient-fetch retry backoff keeps its explicit
`PollDelayMsForTest ?? TransientFetchRetryDelayMs` (different fallback,
nonzero-value conflation documented on the seam, the code-review round's R1).
The static WaitForLocaleBuildToSettleAsync takes the seed as an optional
parameter so SmapiManagement.GetLiveModelJsonAsync (the out-of-class caller,
verified) keeps production pacing. Effect, measured per TFM: the five
delay-paying suites went from 41.3s (37 sync-family tests: every duration a
near-exact multiple of the 500ms pre-delay, the GATEWAY_ERROR retry test 3.0s
with the 2000ms backoff) + 5.2s (42 CatalogManagerTests, ten ~0.5s polling
pins) to 3s combined, 79/79 green; full suite 5078/5078 on BOTH TFMs,
identical to baseline. LibrarySyncServiceTests, StructureTests and
CatalogWiringLocalesTests never reach SMAPI HTTP and keep plain constructors.

Gates: /simplify 4 angles (reuse CLEAN, efficiency CLEAN with the boxing/
allocation adjudication, altitude CLEAN on 4 adjudications; simplification's 3
findings applied: the PollDelayMs computed property, the policy-paragraph trim
keeping the greppable per-file markers, the standardized one-liner wiring
comments). /code-review high: verdict functionally correct (production pacing
verified constant-for-constant, hoisted JSON byte-identical at all four
sites, unwired constructors verified to never reach HTTP); all four findings
applied, all doc-level (the nonzero seam-conflation note, two banned
parenthetical-hyphen prose forms reworded, the param-doc corrected to the
post-F1 PollDelayMs shape, the helper's ONE-claim narrowed to the
per-locale-map shape naming the manifest-only survivor in
SmapiManagementWiringTests); nothing cut, so no JF-740 filing was needed.
Builds: Debug test project and BOTH Release -warnaserror builds (plugin +
tests) clean, 0 warnings 0 errors.
<!-- SECTION:FINAL_SUMMARY:END -->
