---
id: JF-727
title: >-
  JF-727 - two residual per-type hand enumerations outside the JF-706/711 wiring
  table's protection: the item-sourcing block and
  CatalogWiringGraft.ExtractWiring
status: Done
assignee: []
created_date: '2026-10-03 07:59'
updated_date: '2026-10-03 22:07'
labels:
  - catalog
  - code-quality
dependencies:
  - JF-711
references:
  - >-
    backlog/tasks/jf-711 -
    JF-711-the-catalog-id-WRITE-back-in-SyncCatalogForLocaleAsync-is-still-a-per-type-if-chain-the-JF-706-missed-one-residual.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 same-turn from JF-711's /simplify altitude round (findings 1 and 2,
both verified first-hand before filing). Both are the compile-silent "missed one"
class for a fourth synced catalog type, ADJACENT to but NOT protected by the
JF-706/JF-711 recorded context boundary (which covers only the CatalogManager
per-type surface: UpdateInteractionModelAsync's six positional id/version params,
InjectCatalogReferences' three mapping blocks, WarnOnCrossTypeCatalogIds (all of
which DO fail loudly) through signature arity on a fourth type).

1. LibrarySyncService item-sourcing block (~lines 145-157): three hand-written
   FetchLibraryItems calls keyed by per-type BaseItemKind, three
   result.ArtistCount/AlbumCount/SeriesCount assignments, and the three-way
   emptiness conjunction feeding the "No artists, albums or series found" early
   return. Missed COUNT assignment is cosmetic (the completion log and
   CatalogSyncTask report 0); a missed CONJUNCTION TERM is behavioral: a user
   whose library holds ONLY the fourth type's items gets the emptiness skip and
   the sync never runs for them. The fetch itself is compile-loud today (the
   wiring table row consumes the variable). Deeper fix if taken: fold
   `BaseItemKind Kind` into the typeLegs row and drive fetch + counts + the
   emptiness pre-check from the same loop; STOP at the SyncResult DTO (its
   per-type count properties are a public surface not worth generalizing).

2. CatalogWiringGraft.ExtractWiring (~lines 77-91): a per-type if/else matched
   on CatalogSlotTypes.CatalogSlotTypeNames, assigning into six fixed locals
   that feed the six-field CatalogWiring record. Compile-SILENT for a fourth
   synced type: its catalog reference would be silently dropped from the
   extraction, so Apply would re-PUT a rebuilt model UNWIRED for that type
   after every model rebuild (live wiring lost on redeploy) - the silent
   sibling of InjectCatalogReferences' named mapping blocks, in a file the
   boundary text does not name. Note the asymmetry inside the same file: the
   Apply side IS loud (the six positional InjectCatalogReferences arguments),
   so only Extract needs the fix. Fix shape: key the extraction by a
   slot-type-name dictionary (or make CatalogWiring per-type); at minimum the
   boundary note recorded in JF-711's task file must name ExtractWiring
   explicitly so a fourth-type editor knows it is silent there.

GATE-MARKER TAIL (2026-10-03, orchestrator review of commit 8d337ac5, 3 findings; all
five scrutiny axes verified directly against the artifacts: the payload-byte order clean
(table rows literally Artist/Album/Series, the for-loop fetches in order, per-type hashes
unaffected; the interleaved count/fetch has no observable difference on a mid-loop throw),
the reverse lookup loud-on-duplicate confirmed with the three current values distinct, the
JF-716 constraint held (the six *CatalogId accesses never left the lambdas; SyncResult has
exactly three writable counts never re-set outside them), and the JF-737 no-cheap-pin
premise honest): F1 APPLIED (the fetch pin's doc states the METHOD-granular limit - a
re-fetch loop moved INTO SyncUserLibraryAsync's own locale foreach keeps the pin green);
F2 APPLIED (the count pin's doc names the generic *Count family predicate and its
per-addition judgment); F3 APPLIED (the reverse map's construction-time-view contract
documented at the field: no writer today, a mutation feature must rebuild in the same
change). Independent suite 5072/5072 both TFMs on the worker commit; the rebased
merged-tree split run follows.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (solution build, both TFMs, 0 errors 0 warnings; CI's exact Release command `dotnet build --configuration Release -warnaserror` also 0/0 on the final state)
- [x] #2 dotnet test passes (final post-gate state: 5072/5072 net9.0 and 5072/5072 net10.0, `dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1` exit 0; baseline 5067 + 5 new pins, prediction matched; the pre-gate state ran 5071/5071 both TFMs before the two review rounds added the scalar-name pin and reworked no counts)
- [x] #3 No new compiler warnings introduced (every build grepped clean: Debug solution, test project, Release -warnaserror; the 8-element tuple rows, the ref-local sourcing loop, the reverse-lookup dictionary, and the consolidated pin helpers produced no analyzer hits)
- [x] #4 N/A (no session attributes touched; catalog wiring-table and graft only)
- [x] #5 N/A (no HttpClient construction or BaseAddress change)
- [x] #6 N/A (no interaction model, template, or fixture change)
- [x] #7 N/A (behavior-preserving fold, no new intent or handler; the pins ARE the proof and the existing batteries stayed green UNCHANGED: both JF-706 IL structure pins, the JF-716 whole-assembly accessor pin (the six field accesses stay inside the table lambdas; the new count lambdas touch only SyncResult), the JF-695 isolation battery incl. AllTypesCreated_PersistsEachTypeCatalogId, the JF-495 SeriesTests battery incl. SecondSync_ReusesPersistedSeriesCatalogId and the series-only-library sync (the conjunction's behavioral surface for the current three types), and the 4 JF-717 equivalence-class pins (the payload-hash memo world; row order preserved as fetch order, 26-test filtered battery green both TFMs). FIVE new pins added: FetchLibraryItems_HasExactlyOneCallSite_TheSourcingLoop (caller-bound to SyncUserLibraryAsync's own state machine so a per-locale re-fetch cannot pass), SyncResultCountSetters_AreCalledOnlyByTheWiringTableLambdas (derived writable *Count sweep + subset guard), ExtractWiring_IsKeyedByTheSlotTypeTable_TheReverseLookupCallSite, ExtractWiring_EveryCatalogSlotTypeName_ExtractsIntoWiring (the coverage contract: a forward entry without CatalogWiring backing fails, named), ExtractWiring_ScalarNameType_SkipsEntryWithoutThrowing (the review's tolerant-contract fix). Red proofs EXECUTED against real production edits: sabotage A (hand re-expansion of the sourcing block) failed both sourcing pins naming the offenders (4 call instructions / 2 setter call sites); sabotage B (ExtractWiring reverted to the pre-JF-727 if/else) failed the keyed pin while all 16 behavior tests stayed green, the IL pin's exact reason for existing; sabotage C (a temporary fourth CatalogSlotTypeNames entry) failed the coverage pin naming the type; sabotage D (the scalar-name ValueKind guard removed) failed the scalar-name pin with the exact InvalidOperationException the review predicted)
- [x] #8 N/A (no Alexa speech)
- [x] #9 /simplify passed (4 parallel angles; efficiency clean. APPLIED: the shared single-call-site assertion AssertOnlyCallerIs + the RequireMethod lookup helper hoisted into LibrarySyncServiceStructureTests for the graft pin (the JF-582/634/699 no-private-copies discipline, the second consumer had arrived); CountProperties derived ONCE and shared by the subset guard and the setter sweep; the altitude finding, the table comment's fourth-type edit-site list now names the CatalogSlotTypeNames forward entry (forgetting it leaves the fourth type synced but never wired, surfacing only later as InjectCatalogReferences' KeyNotFoundException mid-sync). SKIPPED with reasons: the optional reflection tie forward-entry -> User/SyncResult backing (guards the OPPOSITE direction of the named hazard, its CatalogWiring half already pinned and proven red by sabotage C, and the row-without-forward-entry direction is unreachable from reflection since typeLegs is a method local); the pre-existing SyncCatalogForLocaleAsync pin's GetMethod preamble left untouched (outside the diff's added lines, drive-by); the below-threshold CatalogId/Count sweep generalization (no existing helper; a new generalization, not a reuse))
- [x] #10 /code-review high passed (6 findings: 5 APPLIED - the scalar-name GetString() InvalidOperationException closed with a ValueKind guard (pre-existing latent, JF-555 S3 had covered valueSupplier but never a scalar name; red proof D); the fetch pin caller-bound to the state machine MoveNext (a per-locale loop move kept the count-only pin green); the static-field initialization order contract documented on the reverse map (textual initializer order is load-bearing); the duplicate-forward-value loudness documented (ToDictionary ArgumentException at type init is deliberate: two synced types cannot share a slot name; live-model array duplicates stay last-wins); the three call-site assertion variants consolidated onto the one AssertExactlyOneCallSite core (my own simplify fix had created the third copy); 1 FILED as JF-737 - the emptiness pre-check is the one unpinned per-type fact, with the verified reasons no cheap pin exists (behavioral equivalence over the current three types; IlCallScanner's deliberate MethodSpec blindness) and the candidate closures)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle: worker commit 8d337ac5 + gate-marker tail ddd39610 (rebased) + the simplify-round fix 421cf320, merged as f31d0608. Both residual hand enumerations folded behind the wiring table: the item-sourcing block (rows gaining Kind/Items/StoreCount; one for-loop fetch in row order preserving the JF-717 payload hashes; the emptiness pre-check reading typeLegs.All) and CatalogWiringGraft.ExtractWiring keyed off the new TryGetCatalogTypeForSlotTypeName reverse lookup (loud-on-duplicate at type init; the construction-time-view contract on the field with the forward map's mutability cross-reference added by the simplify round). Five pins including the CALLER-BOUND fetch pin (its method-granular limit stated honestly after the gate-marker) and the fourth-entry coverage contract; FOUR live red proofs (the if/else revert keeping all behavior tests green while failing the IL pin). JF-737 filed. Worker gates green; the orchestrator gate-marker verified all five scrutiny axes at source with 3 doc findings applied; the closure-gate simplify round ran 4 angles with the F2 trim+wording fix and the cross-reference applied, the two internal overlaps kept as load-bearing with two-angle agreement. Suites: worker 5072/5072 both TFMs (prediction matched) plus the CI-exact Release -warnaserror clean; orchestrator independent 5072/5072; merged-tree 5073/5073 both TFMs exit 0 on both split legs. Production surface changed: deployed in the batched post-closure deploy with JF-723.
<!-- SECTION:FINAL_SUMMARY:END -->
