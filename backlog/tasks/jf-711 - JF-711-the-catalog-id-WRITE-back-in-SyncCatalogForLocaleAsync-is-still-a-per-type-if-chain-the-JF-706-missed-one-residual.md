---
id: JF-711
title: >-
  JF-711 - the catalog-id WRITE-back in SyncCatalogForLocaleAsync is still a
  per-type if chain (the JF-706 "missed one" residual)
status: Done
assignee: []
created_date: '2026-10-02 15:40'
updated_date: '2026-10-03 07:17'
labels:
  - catalog
  - code-quality
dependencies:
  - JF-706
references:
  - >-
    backlog/tasks/jf-706 -
    JF-706-collapse-the-three-SyncTypeLegAsync-call-sites-into-a-loop-over-a-type-tuple-list.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the /simplify altitude round of JF-706 (finding 1 of 1
that survived adjudication). JF-706 single-sourced the READ side of the per-type catalog
wiring: the leg loop, the injection gate, and the UpdateInteractionModelAsync id/version
extraction in `RunLegAsync` all derive from one tuple table whose catalog id is a live
`Func<string?>` getter. But the WRITE side of the same data, two layers down the very call
the loop makes, is still a hand enumeration: `SyncCatalogForLocaleAsync`
(LibrarySyncService.cs, the `if (catalogType == CatalogType.Artist) user.ArtistCatalogId =
catalogId; else if Album ... else if Series ...` block in the catalog-creation branch)
persists a newly minted catalog id per type. That site is compile-SILENT for a fourth
catalog type: a new tuple row without its write-back branch compiles clean, the id is
never stored, the getter stays null forever, and every run re-creates the catalog (a new
SMAPI catalog per run, quota burn, and the injection always wiring the fresh-but-orphaned
id). Same class as the three call sites JF-706 collapsed, opposite direction.

THE WORK: give the JF-706 tuple table a setter alongside the getter
(`Action<string> StoreCatalogId`) and thread it through `SyncTypeLegAsync` into
`SyncCatalogForLocaleAsync`, replacing the CatalogType-keyed if/else with the passed
setter, so the table is the single source for BOTH reads and writes of the stored id.
Mind the pins: the JF-695 isolation battery, the JF-495 SeriesTests battery (including
`SecondSync_ReusesPersistedSeriesCatalogId`, which is the write-back's behavioral pin),
and the JF-706 structural pin all stay green unchanged; `SyncCatalogForLocaleAsync`'s
signature change is private-file-internal, so no external callers. Context boundary (the
JF-706 altitude review's second finding, recorded here so it is not re-litigated): the
CatalogManager per-type surface (`UpdateInteractionModelAsync`'s six positional id/version
params, `InjectCatalogReferences`' three mapping blocks, `WarnOnCrossTypeCatalogIds`) is
deliberately NOT part of this; CatalogType has five members with only three in sync scope
by design, and a real fourth synced type forces those edits loudly through signature
arity. Fold this only when touching the write path for its own sake.

GATE-MARKER TAIL (2026-10-03, orchestrator review of commit 7d634ed5, 1 finding; all
five scrutiny axes verified READ-LEVEL with the reviewer re-deriving all three pin
failure modes against the fake and the injection code, compensating for the worker's
permission-blocked sabotage proof: the swapped setter, the misbound getter/setter pair,
and the swapped row Type each provably fail the pin, the PUT-body half load-bearing for
exactly the misbind shape; Action<string> confirmed wipe-proof by CreateCatalogAsync's
throw-on-null contract; the memo interplay clean with no double-write or stale-id shape;
the tuple arity compile-loud; the JF-727 premises verified and the JF-716 mooting note
grep-confirmed): F1 APPLIED (the banned parenthetical hyphen in JF-727's own prose
fixed, the same pattern the diff's review round had fixed in the pin doc).

JF-727 UPDATE (2026-10-03): the two adjacent silent sites this boundary excluded are no
longer silent. The item-sourcing block in LibrarySyncService now derives from the same
wiring table (each row carries its BaseItemKind and a StoreCount lambda; one loop fetches
and counts, and the emptiness pre-check reads the collection), and
CatalogWiringGraft.ExtractWiring is keyed off a reverse lookup of
CatalogSlotTypes.CatalogSlotTypeNames (TryGetCatalogTypeForSlotTypeName) instead of the
per-type if/else. The boundary itself is UNCHANGED and still not to be re-litigated: the
CatalogManager per-type surface (UpdateInteractionModelAsync's six positional id/version
params, InjectCatalogReferences' three mapping blocks, WarnOnCrossTypeCatalogIds) and the
SyncResult DTO's fixed per-type count set stay deliberately outside, loud through
signature arity and the public-surface stop respectively.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (solution build, both TFMs, 0 errors; only the pre-existing xUnit1030 pair at VideoAudioControllerTests.cs:1337, the other worker's untouched file)
- [x] #2 dotnet test passes (final state: 5012/5012 net9.0 and 5012/5012 net10.0, `dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1` exit 0; baseline 5011 + 1 new pin; the same counts held on the pre-gate state before the two review rounds)
- [x] #3 No new compiler warnings introduced (every post-change build grepped clean apart from the pre-existing xUnit1030 pair; the Action<string> tuple element, the threaded parameter, and the strengthened pin produced no analyzer hits)
- [x] #4 N/A (no session attributes touched; LibrarySyncService wiring only)
- [x] #5 N/A (no HttpClient construction or BaseAddress change)
- [x] #6 N/A (no interaction model, template, or fixture change)
- [x] #7 N/A (behavior-preserving restructure, no new intent or handler; the existing pins ARE the proof and stayed green UNCHANGED: both JF-706 IL structure pins (still exactly one call instruction each; the added parameters do not move call-token counts), the JF-695 isolation battery incl. the artist-null write-back negative, the JF-495 SeriesTests battery incl. SecondSync_ReusesPersistedSeriesCatalogId, and the 4 JF-717 equivalence-class pins; ONE new pin added: LegIsolationTests.SyncUserLibraryAsync_AllTypesCreated_PersistsEachTypeCatalogId, strengthened at review with the model-PUT half (see #10). Proof status, recorded faithfully: the pin was NOT run red against a sabotaged wiring (the session's permission layer blocked the deliberate cross-wired-setter edit), so its discriminating power rests on the mechanical facts that the fake mints a distinct id per catalog name (CatalogIdForName) and that a misbound getter or swapped row Type forwards a null id the PUT-body asserts then cannot find; both arguments are static, not executed)
- [x] #8 N/A (no Alexa speech)
- [x] #9 /simplify passed (4 parallel angles; reuse and efficiency clean; simplification F1 applied: the JF-711 rationale de-triplicated, the typeLegs table comment is the one owner and the method doc + inline comment trimmed to their non-duplicated mechanical/coupling facts; simplification F2 SKIPPED: passing the row itself to SyncTypeLegAsync would force spelling the whole anonymous 6-tuple into the parameter list or introducing a private record, both beyond the minimal-diff mandate the agent itself judged not warranted, and it would move the live getter read out of the call-site position the JF-706 prose pins; altitude verdict: right depth per the mandate, boundary respected exactly, with 2 real out-of-scope residuals FILED same-turn as JF-727, the item-sourcing block and CatalogWiringGraft.ExtractWiring)
- [x] #10 /code-review high passed (5 findings, all 5 applied: the new pin's missing PUT-body assertions, closing both the misbound-getter shape (getter bound to a different field than its setter passes the field asserts while the minted pair forwards a null id) and the swapped-row-Type shape, via one assertion block on the three valueCatalog ids; Action<string> instead of Action<string?> for the setter (the task file's own design sketch, and the only meaningful input is CreateCatalogAsync's non-null result, so the non-nullable contract bars a future null wipe that would recreate the catalog every run); the table comment now points at JF-727's compile-silent residuals instead of implying the table protects everything; a banned parenthetical hyphen in the new pin's doc comment replaced with a comma)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle: worker commit 7d634ed5 + gate-marker tail 550d67dc, merged as 64a99802. The catalog-id write-back made compile-loud: the JF-706 wiring table's tuple gains Action<string> storeCatalogId beside the getter (three per-row lambdas threaded table -> SyncTypeLegAsync -> SyncCatalogForLocaleAsync), the creation branch's per-type if/else replaced, and a fourth synced type's write-back fails to COMPILE instead of silently missing. Action<string> (not Action<string?>) per the task's own design and CreateCatalogAsync's throw-on-null contract (no future null wipe). The six field accesses now live only in the table rows; JF-716's original RunLegAsync-scoped pin is moot and the whole-class closure shape is strictly easier (noted in both task files); JF-727 filed for the two residual hand enumerations. One new pin; its sabotage red proof was permission-blocked and honestly reported, and the gate-marker compensated by re-deriving all three failure modes read-level (swapped setter, misbound pair with the PUT-body half load-bearing, swapped row Type). Worker gates green (simplify 1 applied + 2 filed as JF-727 + 1 skipped; code-review high 5/5 applied). Orchestrator gate-marker verified all five scrutiny axes read-level; its 1 finding (the JF-727 prose hyphen) applied. Suites: worker and orchestrator independent 5012/5012 both TFMs, merged-tree 5013/5013 both TFMs exit 0 on both split-TFM legs. Production surface changed (LibrarySyncService): deployed in the post-closure deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
