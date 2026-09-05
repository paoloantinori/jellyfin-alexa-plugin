---
id: JF-493
title: >-
  SeriesName slot cannot fill real series (static 8-value seed, no series
  catalog) + missing numbers-first and infinitive sample shapes: TV intents
  unreachable for non-seed series
status: Done
assignee: []
created_date: '2026-09-05 11:59'
updated_date: '2026-09-05 13:35'
labels:
  - tv
  - catalog-sync
  - nlu
dependencies: []
references:
  - 'corr window 13:52 2026-09-05'
  - JF-324
  - 'CatalogSlotTypes.cs:37'
  - 'LibrarySyncService.cs:117'
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-05 device tests (JF-324 part-1 verification): both 'metti il prossimo episodio di adolescence' and 'metti la stagione 1 episodio 1 di adolescence' NEVER REACHED the skill (no intent request logged, no FallbackIntent: the phrasing family is claimed by the device's video skill competition) AND the model cannot route them anyway: profile-nlu selects AMAZON.FallbackIntent for the canonical 'metti il prossimo episodio di adolescence' because the series_name slot is typed SeriesName, a STATIC 8-value seed list (Breaking Bad, Il Trono di Spade, ...) that cannot absorb any real library series. LibrarySyncService uploads only CatalogType.Artist and CatalogType.Album (lines ~117-122); CatalogSlotTypes.Series = 'SeriesName' exists but nothing feeds it. The one-shot infinitive shape 'di mettere il prossimo episodio di adolescence' lands on PlayArtistSongsIntent with musician='adolescence' (greedy slot absorbs what SeriesName cannot). Also 'metti la stagione 1 episodio 1 di X' is a numbers-FIRST shape with no sample: PlayEpisodeIntent samples are series-first ('Metti {series_name} stagione {season_number} episodio {episode_number}'); profile-nlu shows PlayEpisodeIntent CONSIDERED but not selected for the numbers-first phrasing.

FIX (three parts):
1. SERIES CATALOG: extend LibrarySyncService with a third SyncCatalogForLocaleAsync call for CatalogType.Series (series names from the user's Show libraries, following the artist/album pattern incl. PhoneticSynonymGenerator for English titles under non-English locales), a User.SeriesCatalogId persistence field (XmlSerializer-safe), and the series catalog reference in the UpdateInteractionModelAsync patch. The catalog is what lets real series names fill the SeriesName slot.
2. SAMPLE SHAPES (it-IT via template + 16 locales JSON): numbers-first PlayEpisode family ('{imperative} la stagione {season_number} episodio {episode_number} di {series_name}' and the infinitive one-shot twin) and the infinitive one-shot family for PlayNextEpisodeIntent ('{infinitive} il prossimo/l'ultimo episodio di {series_name}') using the existing imperative/infinitive vocab products.
3. Fixtures + verification: profile-nlu probes for 'metti il prossimo episodio di <real series>' (selected PlayNextEpisodeIntent, slot filled), the numbers-first explicit shape, and the infinitive one-shot shapes; NLU fixtures for the new shapes; device retest card (bare in-session, and 'chiedi a mia collezione di...' one-shot; NOTE the bare phrasing may still be claimed by the device video skill - platform competition, same class as the music-service claim - so the invocation-prefixed form is the reliable path).

Evidence: corr window 13:52-13:54 2026-09-05 (LaunchRequest+NoIntent pairs, zero episode intents), profile-nlu outputs quoted above, model_it-IT.json SeriesName static list, LibrarySyncService.cs:117-122.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
IMPLEMENTED (JF-493 worker, 2026-09-05; status stays In Progress, orchestrator closes after deploy/verification).

PART 1 (series catalog): third `SyncCatalogForLocaleAsync` call for `CatalogType.Series` in LibrarySyncService (BaseItemKind.Series via the shared `FetchLibraryItems`, so the JF-457 strict library scope `includeItemsByName: false` + user TopParentIds scoping + 50000 cap apply unchanged; `SlotValueHelper.Truncate` and `PhoneticSynonymGenerator` come free through the shared `CatalogPayload.FromItems` path). `User.SeriesCatalogId` added next to Artist/AlbumCatalogId (string?, XmlSerializer-safe, no Dictionary). Early-return guard and the model-update gate extended to series; `SyncResult.SeriesCount` added; CatalogSyncTask success log now reports series too (review finding). CatalogType dispatch-site audit (grep of every reference): CatalogSlotTypes.CatalogSlotTypeNames gained `[Series] = "SeriesName"` (the ONLY new dispatch site; unlike AlbumName it-IT-only, SeriesName is declared by all 17 models, so the injection REPLACES the static seed type in place with ReplacesType null and no slot re-typing, and no dialog mismatch is possible since slots keep SeriesName); `CatalogSlotTypes.Names` (dynamic entity target) already mapped Series; DynamicEntityBuilder already builds Series values; CatalogPayload/CatalogValue.FormatId are type-agnostic ("jellyfin_series_"); SmapiManagement, config.html, and all Controllers have zero catalog-type branches (verified by grep); no other consumer reads Artist/AlbumCatalogId. UpdateInteractionModelAsync + InjectCatalogReferences signatures gained (seriesCatalogId, seriesCatalogVersion) positioned with the other ids/versions; the no-catalogs guard covers series (a music-only user with no series sends null and the injection skips it, unit-proven).

PART 2 (sample shapes): it-IT via the YAML template, both episode intents MOVED from explicit_intents to the templates section so the new families use the imperative/infinitive vocab products (the generator only expands vocab refs in templates; existing samples kept verbatim as literal template lines, so the series-first family stays the curated Riproduci/Suona/Metti trio). PlayEpisodeIntent gains the numbers-first family: `{imperative}` + `{infinitive}` x "la stagione {season_number} episodio {episode_number} di {series_name}" (10 samples, 13 total). PlayNextEpisodeIntent gains the infinitive twins "il prossimo episodio di", "l'ultimo episodio di", "la serie {series_name}" (15 samples, 22 total; "la serie" noun stays in the carrier, no bare `{infinitive} {series_name}`). Regen diff inspected per anti-pattern #6: no other intent changed; the only structural delta is the two intents' position within the intents array (irrelevant to Alexa/validators). The other 16 locales: the numbers-first IMPERATIVE shape already existed in all of them (asserted programmatically for es-ES/es-MX/es-US/ja-JP/nl-NL/pt-BR/ar-SA/hi-IN); the infinitive one-shot twins were added ONLY to the 8 locales that carry an infinitive convention anywhere in their model, mirroring each locale's own imperative inventory and marker: en-* "to ..." (2 PlayEpisode + 7 PlayNextEpisode), de-DE "Zu ..." (2 + 4; the separable continue form "schau X weiter" has no clean Zu-twin and was skipped), fr-FR/fr-CA "De ..." (2 + 5). es/ja/nl/pt/ar/hi have NO infinitive layer in any intent (inventing one only for episode intents would violate "follow each locale's existing conventions"); their one-shot delivery is already covered by the existing base-form samples. No slot names/types changed anywhere (SeriesName + AMAZON.NUMBER/ItalianNumber as before); no bare carriers; no AMAZON.SearchQuery.

PART 3 (tests/fixtures/gates): LibrarySyncServiceTests query counts 2 to 3 plus a new series-query shape test (IncludeItemTypes=[Series], TopParentIds scoping, Limit 50000); new Catalog/LibrarySyncServiceSeriesTests runs the FULL sync through the real CatalogManager against a fake SMAPI HTTP handler (catalog create/version/poll/model GET+PUT): proves "Jellyfin Series" creation, SeriesCatalogId persistence, version upload to that id, SeriesName static-seed REPLACEMENT in the model PUT, second-sync reuse (no re-create, same id), and the no-series early return; CatalogManagerTests updated for the new signature + 2 new injection tests (seed replacement in place, no slot re-typing). NLU fixtures: it-IT 4 new PlayEpisode (numbers-first imperative slot-pinned on seed series + intent-level on "adolescence", infinitive twin) and 4 new PlayNextEpisode (prossimo/ultimo/serie infinitive twins slot-pinned, adolescence intent-level); en-US 3 (numbers-first + next-episode + continue-series "to ..." twins). Fixtures are written for the POST-catalog-sync state: the "adolescence" (non-seed) cases are intent-level by design because the SeriesName fill only exists after the series catalog deploys. VOICE_COMMANDS.md Play Episode rows updated for the 9 touched locales (cross-checked: every model sample now appears in the mirror); the intent has no PlayNextEpisode rows in ANY locale (pre-existing JF-324 mirror gap) so JF-494 was filed rather than expanding this diff into 17 new doc rows.

VERIFICATION: dotnet build 0 errors 0 warnings; dotnet test 3269/3269 green (baseline 3263 + 6 new), 5 consecutive clean full-suite runs; validate_interaction_models PASS at the 90-warning baseline; validate_locales PASS; validate_versions PASS; NLU dry-run 8 passed/915 skipped. One observation for the record: a single full-suite run under heavy machine load reported 1 failed/3268 passed once; the name was not captured (output piped to tail) and it never reproduced across 5 subsequent runs including 4 full-suite runs and 5x stress of the changed test classes; the changed tests have no timing dependence. Gates: /simplify applied 3 findings (StubHttpClientFactory reuse, dead GC.SuppressFinalize, explicit else-if for Series assignment); code-review (review-local methodology run directly, no sub-agents) found and fixed 2 findings >= 80 (stale VOICE_COMMANDS rows, CatalogSyncTask log missing series count), filed JF-494 below threshold.

REMAINING for the orchestrator: deploy DLL + rebuild/deploy models (rebuild-models skill), let CatalogSyncTask run (or force it) so SeriesCatalogId populates and the SeriesName catalog-backed type lands in the live models, then the live profile-nlu probes from the task description ("metti il prossimo episodio di adolescence" selecting PlayNextEpisodeIntent with the slot FILLED, numbers-first explicit shape, infinitive one-shots) and the device retest card (invocation-prefixed one-shot is the reliable form; bare phrasing may stay claimed by the device video skill, platform competition).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented, reviewed, deployed and live-verified 2026-09-05 (commit 3d206c27). Third catalog (CatalogType.Series -> SeriesName, 61 series uploaded on the live vendor) following the artist/album pattern: LibrarySyncService series sync, User.SeriesCatalogId persistence, in-place static-seed replacement (ReplacesType=null; the only SeriesName slots are the two episode intents, all 17 locales). Sample shapes: it-IT numbers-first families via template, infinitive one-shot twins in the 8 infinitive-convention locales. Deployed: DLL ca5abaed, catalog sync force-run after clearing LastCatalogSync (XML edit with backup; the 12h dedup gate skips manual triggers). LIVE-VERIFIED via profile-nlu post-sync: 'metti il prossimo episodio di adolescence' -> PlayNextEpisodeIntent with series_name ER_SUCCESS_MATCH 'Adolescence' (jellyfin_series catalog id); 'metti la stagione 1 episodio 1 di adolescence' -> PlayEpisodeIntent with season_number + series_name filled. The two adolescence it-IT fixtures' live-suite confirmation and the device retest are the remaining card items. Full suite 3269/3269; gates: /simplify (worker, 3 applied) + formal scoped code-review (no findings at threshold).
<!-- SECTION:FINAL_SUMMARY:END -->
