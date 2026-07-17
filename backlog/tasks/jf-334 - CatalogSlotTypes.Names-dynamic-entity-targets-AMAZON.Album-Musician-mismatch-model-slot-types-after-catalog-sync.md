---
id: JF-334
title: >-
  CatalogSlotTypes.Names dynamic-entity targets (AMAZON.Album/Musician) mismatch
  model slot types after catalog sync
status: To Do
assignee: []
created_date: '2026-07-12 20:04'
updated_date: '2026-07-13 20:15'
labels:
  - dynamic-entities
  - catalog
  - cleanup
  - low-priority
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/CatalogSlotTypes.cs
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
