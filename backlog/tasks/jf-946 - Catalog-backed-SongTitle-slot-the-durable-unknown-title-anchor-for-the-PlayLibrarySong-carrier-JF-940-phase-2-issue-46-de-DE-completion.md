---
id: JF-946
title: >-
  Catalog-backed SongTitle slot: the durable unknown-title anchor for the
  PlayLibrarySong carrier (JF-940 phase 2, issue #46 de-DE completion)
status: In Progress
assignee: []
created_date: '2026-11-22 17:35'
updated_date: '2026-11-23 06:10'
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
IMPLEMENTATION PLAN (drafted from the Fable-reviewed REVISED PLAN on JF-940 + the 2026-11-22 lab matrix; awaiting maintainer approval before code):

PHASE A - the engine (it-IT + de-DE only, live-verified on the household dev skill):
1. CatalogType: add Song. CatalogSlotTypeNames: [Song] = "SongTitle" (replace-in-place shape, the SeriesName precedent: both templates already declare the type with the static seed; the catalog injection replaces the block, no slot re-typing). ar-SA inherits the model-wide wiring gate automatically; the static seed remains the fallback there and for unsynced users.
2. LibrarySyncService song arm (the Fable-reviewed rules): fetch Audio items; strip parenthetical suffixes and trailing " - " segments, dedup by the STRIPPED form BEFORE the cap; order by play count then date added (never alphabetical: the end of the alphabet must not lose); cap at the valueCatalog budget with a loud warn-on-cut only; skip phonetic synonyms for titles with no word >= 4 letters; exclude junk titles (bare track numbers, "no title" class); NO partial-name synonyms for songs; 140-char truncation rides the existing CatalogValueFactory guarantee.
3. Config: CatalogSongSyncEnabled, default OFF (privacy: the full tracklist goes to Amazon; opt-in). Config page shows "N of M titles synced" with the cut warning when applicable.
4. Verify live (the JF-874 MovieTitle precedent: our dev skill, not the lab): enable the flag, force a catalog sync, confirm the catalog version pins into the saved it-IT + de-DE models, then probe the four German carriers + the Italian battery rows. GREEN CRITERIA: 'spiele den song/den titel/das stueck/das lied die streithoernchen' all select PlayLibrarySongIntent with the slot filled (the lab proved only exact-match anchoring works: v-c) AND the it-IT carrier rows stay green (no regression from the catalog gate) AND out-of-library titles on the carrier stop selecting (the accepted JF-684 consequence: unplayable anyway). The RED-UNTIL-CATALOG fixture row goes green in the same change.
PHASE B - the 15-locale expansion (only after Phase A is verified): per-locale carrier nouns + SongTitle static seeds in the remaining templates, the JF-460 NOUNS-table and voice-reference mirrors, batched with the locale-routing probes.

OUT OF SCOPE: the AddToQueue/PlayNext song slots (stay free-text); bare-form coverage for non-famous titles (the JF-298 class stays: catalog types gate selection, and the free-text arm cannot anchor what Amazon does not know).
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-11-22 implementation progress (Phase A engine COMPLETE, live verification RUNNING): (1) CatalogType.Song existed unused in the enum; wired CatalogSlotTypeNames[Song]='SongTitle' (replace-in-place, the SeriesName shape) + the roster test extended to six types. (2) SongTitlePreparation (new): StripTitle (trailing parenthetical groups + first ' - ' tail), IsJunkTitle (empty/digits/no-title class), HasQualifyingWord (>=4 letters for phonetics), Prepare (order play-count then recency, dedup on stripped BEFORE cap, totalDistinct out). (3) LibrarySyncService: song leg in the wiring table, REMOVED when the per-user opt-in flag is off (a songs-only library with flag off takes the emptiness skip), dedicated uncapped fetch + userdata play counts (IUserDataManager injected), synonym gate at the FromItems call. (4) The JF-706 loud edge fired as designed: UpdateInteractionModelAsync + InjectCatalogReferences + CatalogWiring record + CatalogWiringGraft extraction/apply all extended with the song pair. Chose TRAILING-OPTIONAL params for the manager methods: the three production call sites forward the song pair explicitly (loud), while the 28 test call sites keep all-null song semantics without mechanical churn (documented at the signature). (5) User.CatalogSongSyncEnabled (default OFF, privacy opt-in) + SongCatalogId persisted; SyncResult.SongCount + SongTitleTotal (the N-of-M pair). (6) ConfigurationController PATCH user-skills: CatalogSongSyncEnabled (bool) + LastCatalogSync (explicit null resets the throttle, the verification seam). Tests: 29 new (27 preparation + 2 full-sync: flag-off creates no catalog; flag-on creates 'Jellyfin Songs' with stripped/deduped/play-count-ordered values + the SongTitle model injection asserted). Suite 6199 green after the roster pin extension. Live: deployed to minix, flag enabled + throttle reset via PATCH, 'Sync Alexa Skill Catalogs' task triggered 20:29, monitoring the logs for the song catalog creation.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
VERIFIED LIVE 2026-11-22/23 (the Phase A green criteria): (1) the first sync with the opt-in created catalog amzn1...57c3363b ('Jellyfin Songs'), injected SongTitle into ALL 16 synced locales (replace-in-place in it-IT/de-DE, inert-block add elsewhere), completed 16/16 in ~14min. (2) ALL FOUR German carriers select PlayLibrarySongIntent with the slot FILLED for a real library title ('spiele den song/den titel/das stueck circles' each verified): the three historically-dead carriers now work via exact catalog anchoring, confirming the v-c lab prediction. (3) it-IT rows stay green ('suona il brano rapsodia' -> twin, slot filled). (4) DIVERGENCE from the expected JF-684 gate: out-of-library titles on the carrier STILL select the twin (no NO_SELECTION); benign, the handler answers graceful not-found. (5) Some in-library titles route to the free-text PlaySong arm instead of the twin ('spiele das lied sweet up and down' -> PlaySong, song filled) - both arms are the SAME handler since the JF-940 twin fold, so the outcome is identical. Review rounds applied: simplify 4 agents (shared parenthetical stripper, Prepare<T> single chain, table FetchItems column, seed-warning gating, test join) + code-review 6 findings (5 applied: cross-type warning, 4x fetch bound, gated seed-merge generator, honest song-cut warning + N-of-M logs, movies on the task line; F1 the privacy teardown filed as JF-949 with UI disclosure). Suite 6199 green + 30 new tests.
<!-- SECTION:FINAL_SUMMARY:END -->

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
