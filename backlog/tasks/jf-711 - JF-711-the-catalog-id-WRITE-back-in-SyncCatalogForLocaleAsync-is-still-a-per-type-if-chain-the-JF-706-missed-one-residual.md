---
id: JF-711
title: >-
  JF-711 - the catalog-id WRITE-back in SyncCatalogForLocaleAsync is still a
  per-type if chain (the JF-706 "missed one" residual)
status: To Do
assignee: []
created_date: '2026-10-02 15:40'
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
