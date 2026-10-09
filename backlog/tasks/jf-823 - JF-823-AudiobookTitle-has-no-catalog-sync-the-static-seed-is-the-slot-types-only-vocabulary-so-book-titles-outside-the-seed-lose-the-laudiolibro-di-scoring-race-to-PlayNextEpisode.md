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
- [x] #1 AudiobookTitle added to the static catalog family with the JF-727/JF-706 consistency points honored
- [ ] #2 Live A/B on the JF-684 selection-gating risk (titles outside the catalog must not degrade to NO_SELECTION) before shipping the wiring
- [ ] #3 Live probe matrix: library book titles route PlayBookIntent with slot filled; guards green
<!-- AC:END -->

Worker note (2026-10-09): AC#2 and AC#3 are the ORCHESTRATOR's (live SMAPI probes + a real
catalog sync are out of the worker's no-deploy boundary). AC#1 is complete; the worker-scope
detail is in the Implementation Notes addendum below.

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The 2026-10-08 probe matrix and the diagnosis live in JF-516's notes (the
"JF-816 RESIDUAL CLOSED" section). The static-seed extension that shipped as the
interim fix: templates/it-IT.yaml AudiobookTitle values.
<!-- SECTION:NOTES:END -->


## Implementation Notes (JF-823 worker addendum, 2026-10-09)

**Seed-survival decision (deliverable 4): the AUDIOBOOK SEED ARM was added, per the task's
explicit instruction.** `CatalogSeedEnrichment.GetSeedNames(CatalogType.Audiobook)` now
returns the 22-value it-IT AudiobookTitle block (8 Italian classics + the 14 English titles
of the JF-816 residual fix), read from the embedded model_it-IT.json exactly like the album
arm. The other 16 locales are DELIBERATELY SKIPPED (the Series-style skip): their entire
AudiobookTitle vocabulary is a single generic word ("audiobook", "Hoerbuch", "audiolibro"),
not a title list; seeding them would add the generic words themselves as catalog values.
The skip rationale lives on the renamed `ItItSeedLocale` const (was `AlbumSeedLocale`,
generalized to serve both types). Without this arm the first sync after wiring would
REPLACE the live model's 22-value block with library-only values (CatalogWiringGraft is
replace-in-place); with it, the seed rides every catalog upload and the replace cannot
strip it. Pinned end to end: `MergeInto_AudiobookPayload_GainsItItSeedWhenNotInLibrary`
(unit) and `SyncUserLibraryAsync_AudiobookPayload_CarriesTheItItSeed_BeyondTheLibrary`
(full-sync, reads the payload SMAPI would have fetched out of the CatalogController cache).

**JF-543 gate verdict (deliverable 5): the locale gate is MODEL-WIDE, not per-type, and
the audiobook wiring INHERITS it.** `LibrarySyncService.SyncUserLibraryAsync` filters
`IsCatalogWiringSupported(locale)` before any per-type work, and
`CatalogManager.UpdateInteractionModelAsync` refuses the whole injection for unsupported
locales; `CatalogWiringGraft.Apply` refuses re-application the same way. ar-SA therefore
keeps its embedded model (AudiobookTitle stays the single generic word there). Pinned by
`SyncUserLibraryAsync_ExcludesCatalogWiringUnsupportedLocale_FromAllTraffic`.

**JF-706 arity sites touched (deliverable 1), all compile-forced and verified:**
CatalogWiring record (+AudiobookId/AudiobookVersion + Any), CatalogWiringGraft.ExtractWiring
construction + Apply, CatalogManager.InjectCatalogReferences (+audiobook arm + ReplacesType
null), UpdateInteractionModelAsync (+audiobook id/version + the all-null skip check),
WarnOnCrossTypeCatalogIds, LibrarySyncService typeLegs row + the Minted(...) call, and
17 InjectCatalogReferences + 9 UpdateInteractionModelAsync + 2 CatalogWiring test call
sites plus the 4 query-count assertions (3 to 4). The JF-727 reverse map derived the new
name automatically; pinned in CatalogSlotTypesTests.

**DoD evidence:** red-first pins (forward entry, reverse-map derivation, seed arm: 6
failures on the unmodified tree); full suite 5540/5540 BOTH TFMs from one command;
Release -warnaserror build 0 warnings; /simplify (4 findings: 3 applied - tautological
assert deleted, duplicated ReplacesType rationale shrunk to a pointer, table-row comment
shrunk to one line; 1 applied as formatting - the UpdateInteractionModelAsync test calls
restored to the 3-line family shape; Reuse/Altitude/Efficiency angles clean, no findings);
/code-review high gate recorded in the task's Final Summary.

**Orchestrator handoff:** AC#2 (JF-684 live A/B) and AC#3 (live probe matrix) need a
deployed build + a real catalog sync (the wiring goes live only when the next sync mints
and pins a new catalog version, the JF-684 spike's revert-path lesson). The static seed
stays as the fallback vocabulary either way (the seed arm above).

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes (5540/5540 both TFMs, one command)
- [x] #3 No new compiler warnings introduced (Release -warnaserror 0/0)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session surface touched; the new User.AudiobookCatalogId is the plain string DTO, XmlSerializer roundtrip pinned)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient surface touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: committed models/templates untouched by this worker; the catalog sync edits the LIVE model, which is the orchestrator's live A/B)
- [x] #7 E2E test added for new intent or handler logic (N/A: needs the live endpoint; AC#2/#3 are the orchestrator's probes)
- [x] #8 Locale response strings added to all 17 locales (N/A: no strings surface touched)
- [x] #9 /simplify passed (4 findings, all applied or dispositioned; see worker addendum)
- [x] #10 /code-review high passed (no correctness bug; 5 low findings dispositioned same-turn: F4 applied in-scope, F1/F2/F3 filed as JF-826/JF-825 (F1 renumbered from its filing-time JF-824: the fourth same-window number race, main's ledger JF-824 merged first), F5 deliberate with the ownership note in the worker addendum)
<!-- DOD:END -->


**/code-review high disposition (2026-10-09):** no correctness bug found; the
reviewer independently re-ran the affected classes (108/108 + siblings 29/29,
both TFMs). Findings: F1 (DynamicEntityBuilder's session dynamic push still
replaces the now-catalog-backed AudiobookTitle vocabulary at turn 2+; Series
shares the tolerated shape) FILED as JF-826 (renumbered, see its header); F2 (FromItems has no same-title
dedup; single-file + chaptered editions of one book upload twice) and F3 (the
"Truncated {Type} catalog" warning never truncates anything) FILED as JF-825;
F4 (the LegIsolation fake's CatalogIdForName had no "Jellyfin Audiobooks" arm)
APPLIED in-scope (one arm + the AudiobookCatalogId const); F5 (the exact-set
seed pins couple to whoever owns templates/) is the deliberate drift tripwire:
OWNERSHIP NOTE - any template-side edit to the it-IT AudiobookTitle values must
update GetSeedNames_Audiobook_SourceIsItItModel and the two count-based asserts
in LibrarySyncServiceAudiobookTests in the same change, because the seeds are
SOURCED from the embedded model, so a pin failure there reads "template edit
without its seed-pin update", not "JF-823 broke".

GATE-MARKER AMENDMENT (2026-10-09, orchestrator /code-review high on the
branch, five axes all verified clean, six findings dispositioned):

- APPLIED AT THE TAIL: finding 1 (the settle-poll budget raised 90 -> 120
  iterations for the fifth serialized build per locale, sized on queue depth
  per the comment's own history); finding 4 (the stale "six positional" /
  "three stored catalog ids" narratives corrected to eight/four in
  LibrarySyncService and the JF-716 StructureTests doc).
- REFUTED: finding 2 (silent-empty audiobook seed extraction) is unreachable
  in a green build: the exact-set pin (LibrarySyncServiceAudiobookTests:197)
  reads through the PRODUCTION GetSeedNames path, so a stripped block or
  renamed resource goes red at build time; the runtime warning is a second
  line of defense, not the only one.
- FOLDED INTO AC#2's LIVE A/B (the orchestrator's post-deploy job): finding
  3 sharpens the probe matrix. Beyond out-of-catalog titles in it-IT, the A/B
  MUST cover the 16 non-it locales' GENERIC-WORD fill: post-sync their
  AudiobookTitle vocabulary is catalog-only (library titles + nothing), so a
  de-DE "spiel hörbuch" (no title) may select NO intent per the JF-684
  selection-gating behavior where the static generic word used to match.
  Probe: bare generic-word book requests in de-DE (and one more locale)
  before/after the first sync; if they degrade to NO_SELECTION, the fix is
  the Series-style skip REVERSED for a generic-word seed arm (add each
  locale's own generic word as a catalog value) or a per-locale static-word
  survival mechanism.
- JF-825: finding 5 recorded there (the 22 seeds ride the unbounded side of
  the MaxCatalogValues fetch).
- JF-826: finding 6 confirms it with a sharper scenario (the session dynamic
  push replaces the freshly synced catalog vocabulary MID-CONVERSATION, so
  catalog-wired titles outside the dynamic budget stop routing one-shot for
  the rest of the session).

LIVE A/B VERDICT (2026-10-09 03:30, orchestrator, AC#2's probe matrix run after
the first real sync with the wiring: 16/16 locales, 383 audiobooks, 21 seed
values appended live [one of the 22 deduped against a library title, coverage
unchanged]):

- AC#1 MET: "metti l'audiolibro di sapiens" (library-held, out-of-seed) ->
  PlayBookIntent. The seed titles survive ("il piccolo principe" routes). The
  album guard holds. THE PRIMARY USE CASE WORKS LIVE.
- AC#2 RISK MATERIALIZED, both shapes:
  (a) OUT-OF-CATALOG tails misroute (not NO_SELECTION but sibling steals):
  "xyzzyfoo" -> PlayArtistSongsIntent, "storia del tempo" (a real book NOT in
  the library) -> PlayNextEpisodeIntent (the original JF-816 wrong-item
  shape back for out-of-library titles). Tradeoff vs pre-wiring: in-library
  titles were broken then and work now; out-of-library titles were a clean
  handler not-found then and misroute now (both fail; neither plays wrong
  content for nonsense, but storia-del-tempo class can fuzzy-launch).
  (b) GENERIC-WORD fills degrade in non-it locales: de-DE "lies ein
  hörbuch" -> NO_SELECTION, "spiel das hörbuch" -> MediaInfoIntent; en-GB
  "play an audiobook" -> NO_SELECTION (en-US "play the audiobook" survives).
  Pre-wiring the static generic word matched; the seed skip left nothing.
- FIX DECIDED (the marker's finding-3 option): the generic-word seed arm
  REVERSED from the Series-style skip - append each non-it locale's own
  generic audiobook word as a catalog value (a 16-entry per-locale table in
  CatalogSeedEnrichment, sourced like the position words, not from a model
  block). Out-of-catalog misroute (a): recorded as the accepted JF-684
  tradeoff for now; the handler-side guard for PlayNextEpisode's polluted
  series fuzzy-match is the separate open question (the JF-816 handler
  lesson), filed below.
- ALSO CONFIRMED live in the same battery: the JF-814 competition steal -
  "l'episodio successivo di sailor moon" -> PlayEpisodeIntent (statistical,
  not a word-collision: all 15 new samples carry the number slot; the
  NextUp intent simply lacks the ARTICLE form "l'episodio {position} di
  {series}"). Fix: article-form PlayNextEpisode samples (it-IT now, audit
  the 16 mirrors); handler-equivalent meanwhile (empty numbers fall to the
  same NextUp core), but the guard pin is red until fixed.
