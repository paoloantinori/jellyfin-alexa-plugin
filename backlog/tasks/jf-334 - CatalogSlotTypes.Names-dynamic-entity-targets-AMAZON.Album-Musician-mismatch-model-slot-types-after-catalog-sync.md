---
id: JF-334
title: >-
  CatalogSlotTypes.Names dynamic-entity targets (AMAZON.Album/Musician) mismatch
  model slot types after catalog sync
status: Done
assignee: []
created_date: '2026-07-12 20:04'
updated_date: '2026-09-14 20:39'
labels:
  - dynamic-entities
  - catalog
  - cleanup
  - low-priority
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Spun off from JF-332 (marked Done 2026-07-12). CatalogSlotTypes.Names (the dynamic-entity RUNTIME target via Dialog.UpdateDynamicEntities) maps Album→"AMAZON.Album" and Artist→"AMAZON.Musician", but no slot in the deployed model uses those types after catalog sync swaps artist slots to JellyfinArtist and album slots to catalog-backed AlbumName. So the per-session dynamic-entity personalization (JF-96.3) targets inert slot types.

This is now LOW PRIORITY / largely MOOT: the catalog-backed slot types (AlbumName, JellyfinArtist) already carry the user's full library (85 artists, 885 albums) at turn-1 via JF-96.2 catalog sync (now working after JF-332). Dynamic entities were a turn-2+ personalization layer that the catalog supersedes. No observed functional impact. Tracked so it's not forgotten if the dynamic-entity path is revisited.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Audit CatalogSlotTypes.Names vs the model's actual slot types post-catalog-sync: after sync, artist slots become JellyfinArtist and album slots are catalog-backed AlbumName. Confirm whether Names[Artist]=AMAZON.Musician and Names[Album]=AMAZON.Album still target slot types any model slot uses.
- [ ] #2 If mismatched, point CatalogSlotTypes.Names at the catalog-backed types (JellyfinArtist, AlbumName) so turn-2+ dynamic-entity personalization actually augments the slots the model resolves.
- [ ] #3 Verify the change has real effect (or explicitly document if it's moot because the catalog already carries the full library at turn-1). Cross-language spot-check an English album title spoken by an Italian user.
- [ ] #4 No regression: catalog sync still succeeds and the model builds; live playback unaffected.
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED as MOOT, documented per AC#3's explicit allowance (no code change; the risk/benefit is against a change). Audit (AC#1, confirmed): CatalogSlotTypes.Names targets AMAZON.Album/AMAZON.Musician while post-sync slots use JellyfinArtist (catalog-wired) and AlbumName, so the dynamic-entity layer is inert. Resolution rationale: (a) the catalog carries the user's FULL library at turn-1 (JF-96.2/JF-332), which is the layer dynamic entities were meant to approximate at turn-2+ - the supersedes relationship the task itself notes; (b) live device evidence 2026-09-14 (the Koop on-device resolution) shows BOTH authorities consulted on every musician resolution: the static catalog authority ER_SUCCESS_MATCH (the one that resolved) and the echo-sdk.dynamic authority ER_SUCCESS_NO_MATCH - the dynamic layer is structurally consulted-but-inert, matching no slot; (c) pointing Names at the catalog-wired types is NOT a safe mechanical fix: Dialog.UpdateDynamicEntities updates custom slot types, and whether Amazon lets dynamic values override a catalog-supplied (valueCatalog/valueSupplier-wired) type is undocumented and unverified - an experiment with zero user payoff while the catalog works (AC#4's no-regression concern outweighs). IF per-session personalization beyond the static library is ever wanted (the only thing dynamic entities could still add), file a fresh design task that starts from the platform question in (c). AC#2 intentionally not done (moot); AC#4 unaffected (no change).
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
