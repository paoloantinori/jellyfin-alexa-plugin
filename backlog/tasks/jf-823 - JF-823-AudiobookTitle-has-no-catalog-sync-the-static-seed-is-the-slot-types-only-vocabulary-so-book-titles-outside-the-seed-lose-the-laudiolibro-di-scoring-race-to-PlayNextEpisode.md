---
id: JF-823
title: >-
  JF-823 - AudiobookTitle has no catalog sync: the static seed is the slot
  type's only vocabulary, so book titles outside the seed lose the "l'audiolibro
  di" scoring race to PlayNextEpisode
status: In Progress
assignee: []
created_date: '2026-10-08'
updated_date: '2026-10-08 21:54'
labels:
  - nlu
  - audiobooks
  - catalog-sync
  - interaction-model
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/CatalogSlotTypes.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/LibrarySyncService.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed by the JF-816-residual worker (2026-10-08, from the live probe matrix recorded in
JF-516's notes). Structural gap behind the "l'audiolibro di" scoring race; the JF-816
residual fix only papered over it with a wider static seed.

**The gap**: `CatalogSlotTypes.CatalogSlotTypeNames` (the static catalog upload family)
covers JellyfinArtist, AlbumName, and SeriesName, but NOT AudiobookTitle (it appears
only in the dynamic-entities table `Names`, whose runtime push lands turn 2+ via
Dialog.UpdateDynamicEntities, after first-turn selection has already happened). So in
the SAVED model the AudiobookTitle slot type never receives the user's actual library
book titles: its vocabulary is the static seed (8 Italian classics, extended
2026-10-08 with 14 famous English titles as the JF-816 residual fix) for every user,
with no per-user library coverage.

**The live consequence** (100+ profile-nlu probes, all 100% stable): the it-IT trainer
dropped the elided "l'audiolibro di {book}" carrier below PlayNextEpisodeIntent exactly
when the spoken title matched NOTHING in the slot type vocabulary (the tail got
absorbed as series_name with ER no-match; the response even listed PlayBookIntent
first in consideredIntents with the correct fill, yet selected the competitor). Any
vocabulary overlap flipped the race: article-less ("piccolo principe"), partial
("harry potter"), or catalog-fed (the l'album parallel: "mettere l'album the dark
side of the moon" routes PlayAlbum 3/3 because AlbumName is catalog-wired with the
real library). POST-FIX MEASUREMENT (same day, after the 14-value seed extension):
the win GENERALIZED beyond vocabulary overlap; five out-of-seed tails (famous
English titles and even the "xyzzyfoo" nonsense control) all route PlayBookIntent
3/3 each, so the interim fix is not per-title. What it still cannot do: name a book
the user owns but nobody seeded (first turn, before any dynamic-entity push) with
the recognition quality a catalog-fed type gives artists/albums/series.

**Fix shape**: add `[CatalogType.Audiobook] = "AudiobookTitle"` to
CatalogSlotTypeNames (the JF-727 reverse map derives automatically), extend
LibrarySyncService's payload builder to upload the user's AudioBook items (the
literal tuple table plus the User-entity persistence fields the pattern requires:
a new AudiobookCatalogId with the XmlSerializer-safe DTO treatment, following
ArtistCatalogId/AlbumCatalogId/SeriesCatalogId and their Minted() wiring; watch the
JF-706 context boundary: a real fourth synced type forces the CatalogWiring/
InjectCatalogReferences edits loudly through signature arity), and keep the JF-543
locale gate in mind (catalog-wired builds fail in some locales). THE SEED-SURVIVAL
TOUCH POINT (found by the simplify review): catalog wiring uses replace-in-place
semantics (CatalogWiringGraft), so the first sync after wiring REPLACES the live
model's 22-value AudiobookTitle block; the only mechanism that keeps static seeds on
a replaced type is CatalogSeedEnrichment (JF-541), whose GetSeedNames currently
feeds Album and Artist only. Either add an Audiobook seed arm (extract the it-IT
AudiobookTitle block, the AlbumSeedLocale pattern) or record a deliberate
Series-style skip, accepting that the Italian classics plus the 14 English titles
vanish from the live vocabulary at first sync. Also correct in the same change: the
repo-root CLAUDE.md anti-pattern #10 still lists AudiobookTitle among the
"catalog-backed custom slot types populated by CatalogSyncTask", which this task's
premise contradicts. Scope note: the wiring fixes all 17 locales at once (every
locale declares AudiobookTitle wired to PlayBookIntent.book, each with a single
generic word as its entire vocabulary; only it-IT ever carried real titles).
CAUTION before sizing: catalog types gate intent SELECTION (JF-684), so a
catalog-wired AudiobookTitle could make titles outside the catalog select NO intent
at all; that risk needs a live A/B like JF-684's before the wiring ships. The
static seed should stay as the fallback vocabulary either way (via the seed-survival
point above).

**Acceptance**: "mettere l'audiolibro di <any library book title>" routes
PlayBookIntent with the book slot filled on the live it-IT model, including titles
that are in NO static seed, and the regression guards (il libro forms, PlaySong,
PlayNextEpisode series forms) stay green.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 AudiobookTitle added to the static catalog family with the JF-727/JF-706 consistency points honored
- [ ] #2 Live A/B on the JF-684 selection-gating risk (titles outside the catalog must not degrade to NO_SELECTION) before shipping the wiring
- [ ] #3 Live probe matrix: library book titles route PlayBookIntent with slot filled; guards green
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The 2026-10-08 probe matrix and the diagnosis live in JF-516's notes (the
"JF-816 RESIDUAL CLOSED" section). The static-seed extension that shipped as the
interim fix: templates/it-IT.yaml AudiobookTitle values.
<!-- SECTION:NOTES:END -->

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
