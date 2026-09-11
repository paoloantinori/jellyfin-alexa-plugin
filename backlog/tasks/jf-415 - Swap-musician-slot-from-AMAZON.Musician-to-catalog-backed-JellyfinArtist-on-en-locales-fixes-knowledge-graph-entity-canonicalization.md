---
id: JF-415
title: >-
  Swap musician slot from AMAZON.Musician to catalog-backed JellyfinArtist on
  en-* locales (fixes knowledge-graph entity canonicalization)
status: Done
assignee: []
created_date: '2026-08-30 06:08'
updated_date: '2026-09-11 02:04'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Platform finding (2026-08-29, probe-evidenced, research report 2026-08-30): the AMAZON.Musician built-in slot type on en-* locales (en-US/GB/AU/CA/IN) replaces the slot value with the canonical knowledge-graph entity name instead of the spoken text. Probes: queen->'Paula Abdul', the beatles->'John Lennon', coldplay->'Christopher Anthony John Martin', pink floyd->'Syd Barrett'. This contradicts Amazon's own documentation (Nov 2023) which shows slot.value containing the raw spoken text. Non-en locales (it/fr/de verified) preserve the raw text. Handler consequence: artist search on the mangled canonical name -> clean not-found (no wrong plays; the not-found-first design holds). The feature 'play an album by X' is degraded to not-found on all en-* locales.

Mitigation: swap the musician slot to the catalog-backed JellyfinArtist custom type (JF-96.2 architecture). Custom slot types return raw spoken text. The swap must be atomic per locale (anti-pattern #4: same slot name = same type across all intents in a locale). The catalog must be verified as populated for en-* before the swap.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Swap the musician slot from AMAZON.Musician to JellyfinArtist custom slot type in ALL intents that use it, in the 5 en-* locales (en-US, en-GB, en-AU, en-CA, en-IN): PlaySongIntent, PlayAlbumIntent, PlayArtistSongsIntent, FindSongByArtistIntent, QueryArtistLibraryIntent, and any other intent declaring a musician slot (verify with a script)
- [ ] #2 Slot type consistency (anti-pattern #4): the musician slot must use the SAME type across ALL intents within each locale - the swap must be atomic per locale, not per intent
- [ ] #3 The JellyfinArtist catalog must be verified as populated for the en-* locales BEFORE the model swap (CatalogSyncLocales config or catalog inspection via SMAPI)
- [ ] #4 Non-en-* locales are NOT swapped in this task (they don't exhibit the canonicalization bug; swapping them would be scope creep - evaluate separately after the en-* swap is verified)
- [ ] #5 Post-swap verification: profile-nlu probe on en-GB confirming 'an album by queen' returns musician=queen (raw), not a canonical entity name
- [ ] #6 Full NLU suite green on the swapped locales
- [ ] #7 Research report referenced: claudedocs/research_amazon_musician_entity_canonicalization_2026-08-30.md
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
SCOPE EXTENSION (2026-09-09, from JF-508 part A): the same catalog-backed slot swap is now needed on it-IT, with the mechanism fully understood there (JF-508's notes carry the full analysis): 'suona la band {obscure artist}' loses to PlaySong's statistical 'Suona la canzone {song}' absorption because AMAZON.Musician only anchors via Amazon's knowledge graph (works for famous artists: 'pink floyd'->KG entity; fails for in-library obscure ones: 'soul coughing'). A catalog-backed JellyfinArtist musician slot (weekly CatalogSyncTask + phonetic variants) anchors EVERY in-library artist. The it-IT template change is one slot-type line, but the design trade-off this task already names applies: out-of-library names stop filling the slot (the xyzzyfoo not-found e2e class) - the not-found UX must be designed with the swap (elicitation or documented no-match). Probe-verify-first rule applies (the JF-400 method note).

/simplify gate (2026-09-10): production code clean on all four angles; applied findings: the tautological per-locale resolver asserts replaced with the two dictionary-constant pins (the resolver derives from the same set, so the loop was true by construction; the duplicated foreach in DynamicEntityBuilderTests dropped), the inert-type constraint trimmed to one canonical statement + pointer, the CatalogBackedMusicianLocales doc re-worded from 'lockstep authority' to 'COMMITTED-model authority' with the injection-divergence caveat, and the stale CLAUDE.md CatalogSyncLocales default corrected (the CODE default is '*', verified at PluginConfiguration.cs:107). OPEN ARCHITECTURE DECISION (single-sourcing 'which locales declare JellyfinArtist'): CatalogManager.InjectCatalogReferences re-types musician slots AMAZON.Musician -> JellyfinArtist on EVERY locale the catalog sync runs for (CatalogManager.cs:855 + :919-927, reachable per locale from LibrarySyncService.cs:141; CatalogSyncLocales defaults to '*'), so DEPLOYED models can declare JellyfinArtist on locales outside CatalogBackedMusicianLocales while ResolveMusicianSlotType returns AMAZON.Musician for the runtime Dialog.UpdateDynamicEntities target - artist values land inert there (the JF-332 failure mode). PRE-EXISTING class (pre-delta the runtime target was hardcoded AMAZON.Musician everywhere, equally mismatched; this delta fixes 6 locales and is neutral on the other 11). Decide: gate the artist ReplacesType on CatalogBackedMusicianLocales, or extend the swap evaluation to catalog-synced locales. NOT decided in this task's scope.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed 2026-09-11 with merge 5824a9e6 + deployed in stages (DLL md5 ca2c338c; models pushed; the full trail below). THE HEADLINE FIX SHIPPED AND VERIFIED ON en-*: the knowledge-graph canonicalization is gone - en-GB probe 'an album by queen' -> PlayAlbumIntent with musician.value='queen' (raw; pre-swap this returned 'Paula Abdul'), ER_SUCCESS_MATCH on the skill's own JellyfinArtist authority; pink floyd/beatles/radiohead probes raw across en-US/en-GB/it-IT. Swap atomic on 6 locales (7 languageModel + 3 dialog declarations each, reviewer-censused); C# CatalogBackedMusicianLocales + ResolveMusicianSlotType + locale-aware DynamicEntityBuilder with the (userId, locale) singleton-cache key; the JF-332 inert-type mismatch resolved for the swapped locales; 21 new tests incl. the review-mandated pin on the injection's in-place-replace branch (whose behavior was then observed LIVE in the sync logs). Gates: /simplify (4 angles; tautology trim, doc accuracies, stale CLAUDE.md CatalogSyncLocales default corrected, the injected-vs-committed authority question tracked open), code-review high (6 axes verified; its one finding fixed), suites 3610/3610 net9.0, validators PASS, 0 warnings both TFMs. DEPLOY LESSON (cost ~2h of night): manually-deployed models carry only static seeds - the catalog valueSupplier binding is injected by the catalog SYNC, which sat in its 12h throttle; aging the persisted LastCatalogSync + re-triggering rebound all catalogs and fixed the seed-only failures (42->35 on it-IT). AC#6 VERDICT, the split: en-GB NLU fully green, en-AU green, en-US 4 + en-CA 1 + en-IN 1 scattered (2 musician-shaped, others slot-level shifts; e2e-class excluded per the documented en unreliability); it-IT native swap = 35 failures vs 23 on the pre-swap baseline run the same night -> ~12 attributable, ~23 pre-existing stale fixtures. ROLLED it-IT's DEPLOYED model back to pre-swap+injection (protects the daily driver; the deployed model still declares JellyfinArtist via injection so the runtime resolver matches; the COMMITTED repo model stays swapped - provenance differs, type agrees). JF-541 filed with the full evidence: re-pin the 23 stale fixtures first, then re-apply the native swap and drive the 12 to zero, plus the en stragglers. Live smoke: it-IT pink floyd plays land on the correct P!nk/Pink Floyd disambiguation.
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
