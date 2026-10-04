---
id: JF-737
title: >-
  JF-737 - the JF-727 emptiness pre-check is the one unpinned per-type fact:
  no cheap pin exists for the conjunction's collection derivation
status: Done
assignee: []
created_date: '2026-10-03 23:11'
updated_date: '2026-10-04 12:00'
labels:
  - catalog
  - structure-pins
dependencies:
  - JF-727
references:
  - >-
    backlog/tasks/jf-727 - JF-727-two-residual-per-type-hand-enumerations-outside-the-JF-706-711-wiring-tables-protection-the-item-sourcing-block-and-CatalogWiringGraft-ExtractWiring.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 same-turn from JF-727's /code-review high round (finding 1 of 6, the
only one not applied or doc-claused; the round's other five were applied in the JF-727
diff itself). The JF-727 sourcing fold pinned the fetch
(FetchLibraryItems_HasExactlyOneCallSite_TheSourcingLoop, caller-bound to
SyncUserLibraryAsync's own state machine) and the counts
(SyncResultCountSetters_AreCalledOnlyByTheWiringTableLambdas, the JF-716 closure idiom
on the StoreCount row lambdas), but the emptiness pre-check
(`typeLegs.All(leg => leg.Items.Count == 0)` in LibrarySyncService.SyncUserLibraryAsync,
the early "No artists, albums or series found" return) has NO pin of either kind, even
though the JF-727 task file itself calls the missed conjunction term the BEHAVIORAL half
of the fold (a missed count is cosmetic).

The gap: a future edit can rewrite `typeLegs.All(...)` back into a hand three-term
conjunction over `typeLegs[0..2]` with every pin green, and a fourth synced type added
after that silently misses its conjunction term (the repo's exact "missed one" class): a
library holding only the fourth type's items takes the emptiness skip and never syncs.

Why it was NOT closed inside JF-727 (verified first-hand, not assumed):
- BEHAVIORAL pin impossible today: over the current three rows, All() and the hand
  three-term conjunction are behaviorally identical; the distinguishing library
  (fourth-type-only) cannot be constructed because typeLegs is a method-local table and
  CatalogType's sync scope is three members.
- STRUCTURAL pin disproportionate: the derivation's observable IL signature is a call to
  the GENERIC Enumerable.All, emitted as a MethodSpec (0x2B) token; IlCallScanner's
  TryResolveMethod deliberately skips non-MethodDef/MemberRef tables (JF-634's same-
  module comparability rule), so binding "MoveNext calls All" needs a MethodSpec-aware
  resolution with generic context, a scanner extension out of proportion to the hazard.
- The weak closure (wrap the check in a named helper and pin the helper's single call
  site) pins the call-through but not the body's derivation: a hand rewrite INSIDE the
  helper keeps that pin green. Not worth the indirection for no real coverage gain.

Candidate closures, in order of preference: (a) if IlCallScanner ever gains
MethodSpec/generic-call resolution for another pin, add "SyncUserLibraryAsync's MoveNext
calls Enumerable.All over the wiring table" here; (b) when a real fourth synced type
arrives (forcing the CatalogWiring/InjectCatalogReferences arity edits), add the
fourth-type-only-library behavioral pin in the same change, which permanently
discriminates the hand conjunction; (c) accept the residual risk with the current
coverage: the fetch pin catches the realistic partial regressions (any re-expansion to
hand fetches), the conjunction is a one-line collection read whose rewrite-back is an
active edit rather than a passive miss, and the table comment documents the contract.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Debug solution build and CI's exact `dotnet build Jellyfin.Plugin.AlexaSkill.sln --configuration Release --no-restore -warnaserror`, both 0 warnings 0 errors on the final state)
- [x] #2 dotnet test passes (final state: 5110/5110 net9.0 and 5110/5110 net10.0, `dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1`, exit 0; baseline 5107 + 3 new tests, prediction matched exactly; three consecutive full-green runs after a first full run carried ONE non-reproducing net10.0 single-test failure whose name the summary grep discarded - the change touches a pure static predicate and two test files, nothing timing- or TFM-conditional, and two consecutive full runs with trx capture plus one more ran clean)
- [x] #3 No new compiler warnings introduced (every build grepped clean: Debug solution x3, Release -warnaserror x2; the tuple-typed predicate signature, the hoisted state-machine helper, and the null-element array builder produced no analyzer hits)
- [x] #4 N/A (no session attributes touched; a catalog-sync predicate and its pins only)
- [x] #5 N/A (no HttpClient construction or BaseAddress change)
- [x] #6 N/A (no interaction model, template, or fixture change)
- [x] #7 N/A (behavior-preserving extraction, no new intent or handler; the pins ARE the proof and TWO live red proofs were executed against real production edits: sabotage A, the filed hazard itself - the helper body rewritten into a hand three-term conjunction over legs[0..2] - failed the behavioral discriminator on both TFMs while the routing pin and all six other pins stayed green, exactly the case the JF-727 weak-shape analysis said would keep every pin green; sabotage B, the inline revert - the call site back to a raw typeLegs.All() with the helper kept - failed the routing pin on both TFMs with "Found 0 call instructions" while the behavioral tests stayed green. The filtered 25-test battery incl. the JF-495 series-only-library sync, the conjunction's current-three-types behavioral surface, ran green both TFMs)
- [x] #8 N/A (no Alexa speech)
- [x] #9 /simplify passed (4 parallel angles: efficiency CLEAN, altitude CLEAN, reuse clean, simplification 2 findings: the narrative redundancy APPLIED - the four full copies of the hazard story trimmed to one canonical home on the predicate's XML doc plus one for the discriminator half on the behavioral class doc, with one-line pointers in the production call-site comment and the routing-pin doc; the tuple-alias/record finding SKIPPED with reasons: adding a row field is compile-loud at every spelling site (the tuple TYPE mismatches at the AllTypeLegsEmpty(typeLegs) call and the test builder, CS1503), so the cited "missed-one risk" does not hold; the row shape is the deliberate JF-711/JF-727 design; a file-scoped alias does not cross assemblies and would hide the shape from the table's own declaration site. The altitude agent independently declined the record-type refactor as touching the whole JF-727 table machinery)
- [x] #10 /code-review high passed (5 findings, 3 applied / 2 dispositioned: F3 APPLIED - the predicate moved from the error-string helpers region 780 lines away to directly above SyncUserLibraryAsync, with the doc noting a local function under the table would be unpinnable by the behavioral test; F2 APPLIED as a doc sentence - the routing pin's POSITIONAL binding named: the block compiles into MoveNext only while it stays after the method's first await, a move above it fails the pin on an innocent reorder, the conscious-widening path the JF-727 fetch pin already carries; F4 APPLIED as the gate-marker-tail note on the test Legs builder - a contract growing past Items.Count must re-judge the placeholders per addition, and a predicate that starts dereferencing items NREs on the null elements and fails the pins loudly, which is vigilance not a hole; F1 DECLINED with reasons - the tuple alias/record, same disposition as the simplify round; F5 SKIPPED - the production doc's prose references to test names cannot become crefs across the plugin-to-test assembly boundary, prose-exact naming is the established house pattern. No real-but-out-of-scope finding remained, so the reserved JF-746 number was not consumed)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed with a STRONGER shape than the filing's weak closure, and the filing's
disproportion premise corrected by probe. The closure: the inline
`typeLegs.All(leg => leg.Items.Count == 0)` pre-check is extracted into a named
internal static predicate `LibrarySyncService.AllTypeLegsEmpty(legs)` (definition
unchanged: All() over the FULL table) whose parameter makes the JF-727 filing's
"impossible" behavioral discriminator constructible - the filing's impossibility
argument (table method-local, CatalogType sync scope three members) held only at
the sync level; the predicate's parameter is a test-fabricatable collection, so
the distinguishing input (a FOURTH leg holding the library's only items) is built
in the test without any fourth CatalogType. Two pins: the ROUTING pin
(AllTypeLegsEmpty_HasExactlyOneCallSite_TheEmptinessPreCheck, the existing
AssertOnlyCallerIs idiom, expected caller the hoisted
SyncUserLibraryAsyncStateMachine - the fetch pin's state-machine lookup hoisted to
a shared helper at its second consumer) fails the inline revert, and the BEHAVIORAL
discriminator (LibrarySyncServiceEmptinessPrecheckTests, a fixture-free class:
truth table plus the fourth-leg-only case) fails the hand conjunction inside the
helper - the exact case the JF-727 weak-shape analysis conceded would keep the
call-site pin green. Both directions RED-PROVEN live on both TFMs against real
production edits (sabotage A: conjunction-in-helper fails the discriminator with
the other seven pins green; sabotage B: inline revert fails the routing pin with
"Found 0 call instructions" and the behavioral pins green). Why not the filing's
candidate (a): the MethodSpec premise was PROBED, not assumed - plain
Module.ResolveMethod(token, null, null) DOES resolve the 0x2B token of the All()
call to System.Linq.Enumerable.All on both TFMs, so the filing's "needs
MethodSpec-aware resolution with generic context" was overstated; a structural
"MoveNext calls All" pin is nonetheless still the WORSE shape here (it would
falsely reject a legitimate for-loop rewrite that the behavioral pin correctly
accepts, and widening TryResolveMethod's documented JF-634 table filter would
change every existing roster consumer). The JF-724 merge verified NOT to have
touched the pre-check (its LibrarySyncService diff sits entirely in the ledger
writer region past line 470; the check was byte-identical to the JF-727 shape).
Gates: /simplify 4 angles (narrative dedup applied; tuple alias declined with
reasons) and /code-review high 5 findings (3 applied: predicate relocation above
the sync entry point, the positional-binding false-positive shape named, the
test-builder re-judgment note; 2 dispositioned: tuple alias declined as above,
prose test references skipped - no cref crosses the assembly boundary). Suites:
full suite 5110/5110 net9.0 and 5110/5110 net10.0 on the final state (baseline
5107 + 3), Release -warnaserror 0/0; one earlier full run carried a single
non-reproducing net10.0 flake whose name was lost to a summary-only grep, three
subsequent full runs green (two with trx capture). No deploy (the orchestrator's
batched post-closure deploy owns it).
<!-- SECTION:FINAL_SUMMARY:END -->
