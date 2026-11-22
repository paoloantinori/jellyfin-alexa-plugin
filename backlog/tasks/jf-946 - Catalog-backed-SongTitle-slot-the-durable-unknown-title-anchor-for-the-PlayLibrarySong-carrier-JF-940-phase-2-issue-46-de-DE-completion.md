---
id: JF-946
title: >-
  Catalog-backed SongTitle slot: the durable unknown-title anchor for the
  PlayLibrarySong carrier (JF-940 phase 2, issue #46 de-DE completion)
status: To Do
assignee: []
created_date: '2026-11-22 17:35'
updated_date: '2026-11-22 17:37'
labels:
  - nlu
  - catalog
  - issue-46
  - jF-940-followup
dependencies: []
references:
  - scripts/nlu-lab/runs/20261122-182048-jf940-de-vc.json
  - scripts/nlu-lab/runs/20261122-182546-jf940-de-ve.json
  - tests/integration/fixtures/de-DE.yaml
  - claudedocs/research_catalog-backed-song-slots_2026-11-21.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The JF-940 dual-intent proved the SELECTION layer works only when the custom SongTitle type can anchor the spoken title. Lab matrix 2026-11-22 (de-DE, runs jf940-de-final/va/vb/vc/vd/ve): with the final static 10-word generic seed, only the 'das Lied' carrier selects PlayLibrarySongIntent for an unknown title; 'den Song'/'den Titel' select NOTHING and 'das Stück' falls to Fallback. Variant v-c (seed containing REAL LIBRARY TITLES) greens ALL FOUR carriers, but v-e (same seed minus the exact test title) breaks them again: the v-c green is exact-match anchoring, not span training. No static seed content generalizes to unknown titles in de-DE's competition landscape (it-IT tolerates the generic seed: its carrier row is live-green). CONCLUSION: the durable fix for de-DE-class locales is catalog-syncing SongTitle per library (the AlbumName/JF-96.2 architecture: CatalogSyncService + CatalogSlotTypes + phonetic synonyms). Per JF-684 the catalog gate on a play-by-title intent is HARMLESS (an out-of-library title is unplayable anyway; not selecting is the correct outcome). v-c is the lab proof the catalog shape works. Scope: wire SongTitle into CatalogSlotTypes + the sync upload + verify via lab with a per-library catalog version; the RED-UNTIL-CATALOG fixture row (spiele den song die streithörnchen) goes green when it lands. ALSO pending from JF-940: de-DE two-step simulate slot verification was blocked by a per-locale simulate outage (2026-11-22 evening; it-IT evergreen green, de-DE open phrases failed both outage classes); the JF-919 gate in the e2e harness auto-skips during outage windows.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Rollout sequencing: the remaining 15 locales take the PlayLibrarySong carrier ONLY together with this catalog phase (or later), not before: the static-seed shape underdelivers on selection for unknown titles in de-DE's competition landscape, and expanding it untested to 15 more locales would ship a known-partial shape. it-IT is the exception already live and verified end to end (one-shot wrapper -> PlayLibrarySongIntent, slot fills raw, play succeeds; simulate 2026-11-22).
<!-- SECTION:PLAN:END -->

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
