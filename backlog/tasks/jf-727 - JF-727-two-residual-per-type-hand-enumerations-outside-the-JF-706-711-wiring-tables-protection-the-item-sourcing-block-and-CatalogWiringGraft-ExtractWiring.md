---
id: JF-727
title: >-
  JF-727 - two residual per-type hand enumerations outside the JF-706/711
  wiring table's protection: the item-sourcing block and
  CatalogWiringGraft.ExtractWiring
status: To Do
assignee: []
created_date: '2026-10-03 07:59'
labels:
  - catalog
  - code-quality
dependencies:
  - JF-711
references:
  - >-
    backlog/tasks/jf-711 - JF-711-the-catalog-id-WRITE-back-in-SyncCatalogForLocaleAsync-is-still-a-per-type-if-chain-the-JF-706-missed-one-residual.md
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
