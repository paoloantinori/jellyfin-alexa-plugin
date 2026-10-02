---
id: JF-716
title: >-
  JF-716 - structure pin for the injection seam: the six positional
  UpdateInteractionModelAsync arguments can re-expand to hand ternaries with
  both JF-706 pins green
status: To Do
assignee: []
created_date: '2026-10-02 16:10'
labels:
  - catalog
  - structure-pins
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
Filed 2026-10-02 same-turn from the orchestrator gate-marker review of the JF-706 merge
(commit 1a2f7651, finding 3 of 5). The JF-706 structure pins scan the SyncTypeLegAsync
and SyncCatalogForLocaleAsync call tokens and close the leg-loop side, but NEITHER
covers the injection seam: the injection gate (`minted.Count > 0`) and the six
positional id/version arguments into UpdateInteractionModelAsync
(LibrarySyncService.cs ~262-285) can be re-expanded into hand-written per-type ternaries
(the pre-JF-706 shape) or a second gate with both pins green, silently reopening the
JF-495 hand-ternary hazard JF-706 closed on the leg side.

THE WORK: a third structural pin closing the injection side. Candidate shape (from the
review): a NEGATIVE assertion that RunLegAsync's MoveNext body never loads the three
stored-id getters (user.ArtistCatalogId / user.AlbumCatalogId / user.SeriesCatalogId -
the new code reads ids only from the minted collection, so the hand-ternary shape, which
reads those getters, cannot appear without tripping the scan). Mind: IlCallScanner scans
call opcodes (0x28/0x6F); a field-load scan needs ldfld (0x7B) support, so decide
between extending the scanner or a targeted scan helper. The getter IS written
elsewhere (the if/else chain at ~558-569), so scope the assertion to RunLegAsync's body
only. Alternative accepted shape if the scanner extension is disproportionate: a
CatalogManager-side allowlist scan (per the review's alternative), or the JF-711
setter-threading design landing first and making the negative assertion moot - in that
order of preference, not in parallel.
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
