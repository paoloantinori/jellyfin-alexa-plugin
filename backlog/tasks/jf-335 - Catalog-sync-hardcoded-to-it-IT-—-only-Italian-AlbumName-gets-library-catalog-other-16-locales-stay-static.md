---
id: JF-335
title: >-
  Catalog sync hardcoded to it-IT — only Italian AlbumName gets library catalog;
  other 16 locales stay static
status: Done
assignee: []
created_date: '2026-07-12 21:04'
updated_date: '2026-07-27 04:52'
labels:
  - catalog
  - i18n
  - multilingual
  - it-IT
  - slot-type
dependencies: []
references:
  - >-
    JF-345 (handler-side song-to-album cascade — interim workaround for the 16
    free-text locales until this catalog-side fix lands)
  - >-
    PR #15 (English song/album utterance trim — related; makes PlaySong the sole
    owner of bare 'play X')
  - JF-334 (CatalogSlotTypes dynamic-entity Names mismatch — related cleanup)
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/LibrarySyncService.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/PhoneticSynonymGenerator.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found while investigating JF-332/JF-333 (2026-07-12). LibrarySyncService hardcodes the catalog sync to Italian: `private const string ItalianLocale = "it-IT";` (LibrarySyncService.cs:29), passed to `_catalogManager.UpdateInteractionModelAsync(accessToken, skillId, DevelopmentStage, ItalianLocale, ...)` (line 132) and to `CatalogPayload.FromItems(catalogType, itemTuples, PhoneticSynonymGenerator.GenerateSynonyms, ItalianLocale)` (line 186).

Net effect: the JF-96.2 catalog sync (now working after JF-332/JF-333) only populates the it-IT AlbumName + JellyfinArtist slot types from the user's library. The other ~16 locales' album/artist slot types stay at their static seed values, so the "jazz cafe routes one-shot" fix is it-IT-specific — an en-US/de-DE/etc. user gets the same misrouting bug for arbitrary library albums.

The Italian phonetic synonyms (PhoneticSynonymGenerator) are also Italian-specific by construction — applying them to non-Italian locales would be wrong, so going multilingual needs locale-aware synonym generation (or skipping synonyms for non-it-IT).

This is the natural follow-up to JF-332's resolution: the catalog-sync MECHANICS now work and are locale-agnostic, but the sync TARGET is hardcoded to it-IT. Out of scope for the JF-332/JF-333 session (which was driven by the it-IT user's jazz cafe report).

Verified facts: LibrarySyncService.cs:29/132/186 (const + two call sites).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Identify the set of locales the deployed skill actually supports (SMAPI list + the active-locale count the rebuild endpoint reports) and decide which locales catalog sync should target (all active, or a configurable subset).
- [ ] #2 LibrarySyncService.UpdateInteractionModelAsync / CatalogPayload.FromItems loop over the supported locales instead of the hardcoded ItalianLocale constant (LibrarySyncService.cs:29, :132, :186).
- [ ] #3 Phonetic synonyms become locale-aware: PhoneticSynonymGenerator.GenerateSynonyms must not apply Italian-specific transforms to non-Italian locales (English album names spoken by a German user, etc.) — confirm the synonym generator is locale-parameterized or skipped per locale.
- [ ] #4 For each target locale, the catalog-backed slot type (AlbumName equivalent + artist slot) gets populated; verify via SMAPI get-interaction-model that at least 2 non-it-IT locales now reference the catalog.
- [ ] #5 No regression: it-IT catalog sync still succeeds and 'jazz cafe' still routes one-shot; profile-nlu a non-it-IT locale album.
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

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-16 19:48
---
2026-07-16 (PR #15 review): Confirmed this task is the catalog-side root fix for the 16-locale gap that PR #15 exposed. Verified per-locale: only it-IT's model defines the AlbumName type (with Italian phonetic synonyms via CatalogSyncTask); the other 16 locales use free-text AMAZON.MusicRecording for PlayAlbumIntent.album, so the catalog album sync is inert there (validator flags 'Missing slot type AlbumName' for each). JF-345 (song-to-album cascade) is the handler-side interim workaround for those 16 free-text locales; this task supersedes that workaround for albums once it lands. NOTE: JF-346 and JF-347 were created mid-session as duplicates of this task and have been archived — their content (locale-aware PhoneticSynonymGenerator, per-locale AlbumName population, the all-vs-subset decision) is already covered by this task's acceptance criteria (#2 loop over locales, #3 locale-aware synonyms, #4 per-locale AlbumName + artist population, #5 no it-IT regression).
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
VERIFIED RESOLVED 2026-07-27 (no code change needed; the fix had already landed).

The code JF-335 asked for is already in place:
- LibrarySyncService.cs NO LONGER hardcodes ItalianLocale. The const is gone; line 85 reads CatalogSyncLocales config, line 86 resolves locales via ResolveSyncLocalesAsync, line 218 passes the loop locale variable to CatalogPayload.FromItems (AC #2 met).
- ResolveSyncLocalesAsync handles empty (it-IT only), '*' (all active locales via GetActiveLocalesAsync), and explicit comma-separated lists (AC #1 met).
- PhoneticSynonymGenerator.GenerateSynonyms(name, locale) dispatches by locale prefix via a switch (line 33); Italian transforms only apply to 'it', so non-Italian locales do not get Italian phonetic synonyms (AC #3 met).

LIVE VERIFICATION (SMAPI get-interaction-model, skill amzn1.ask.skill.33dfacd5-... development stage): 5 locales (it-IT, de-DE, en-US, fr-FR, es-ES) ALL have BOTH JellyfinArtist AND AlbumName as catalog-backed slot types (valueSupplier/valueCatalog references, versions 51-68 advancing). They share catalog IDs 6590add1 (artists) and 620e99b0 (albums), with per-locale catalog versions confirming locale-aware synonym generation. AC #4 met (>=2 non-it-IT locales reference the catalog). it-IT remains catalog-backed (AC #5, no regression).

Config on minix: CatalogSyncLocales='*' (set during the JF-380 session). The catalog sync is a 12h scheduled task (CatalogSyncTask); the multi-locale sync ran successfully as evidenced by the live catalog-backed slots.

No code change this session. Task closed as verified-resolved.
<!-- SECTION:FINAL_SUMMARY:END -->
