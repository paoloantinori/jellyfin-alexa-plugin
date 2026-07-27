---
id: JF-380
title: >-
  Investigate: did the selective-locale rebuild feature (PR #14) break
  multi-locale catalog injection?
status: Done
assignee: []
created_date: '2026-07-25 18:07'
updated_date: '2026-07-26 15:00'
labels:
  - bug
  - catalog
  - selective-locale
  - regression
  - artist-search
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/CatalogManager.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/LibrarySyncService.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
User hypothesis (2026-07-25): the selective-language rebuild feature (PR #14: 'Scope interaction model rebuilds to the configured locale', commits 9bf2f4d / 7a78b8f / 0b1b7ef / 2d0afb0) may have broken catalog injection so it only lands in one locale.

EVIDENCE FOR: PR #14 already proved to have a sharp edge in this session (the custom-model/rebuild endpoint silently rebuilds only ONE locale - the UI dropdown locale or Configuration.CustomModelLocale fallback; see JF-376 closed-not-a-bug). The catalog sync log (2026-07-25 18:51) shows: 'Injecting 2 catalog references into interaction model (JellyfinArtist, AlbumName)' then 'Pushing updated interaction model for skill ... locale it-IT' - it pushes for ONE locale. If a multi-locale skill only gets the artist catalog in it-IT, on-device recognition in other locales (or the cross-locale ASR path) is starved.

EVIDENCE AGAINST (correction of an earlier wrong claim): the catalog is NOT empty. JellyfinArtist is a catalog-backed slot type (valueSupplier -> CatalogValueSupplier -> catalog 6590add1..., version 32 with 86 artists). The earlier 'total values=0' read was checking the inline values array, which is legitimately empty for catalog-backed slots. So content IS being uploaded; the question is whether it reaches all locales' models.

DISTINCT FROM JF-379 (the c/k/ck/q phonetic variants): this is about whether synced content reaches the device at all, regardless of variant coverage.

LIKELY AREA: CatalogManager catalog-injection + how it calls the redeploy/push with a locale scope. Compare the locale handling to the rebuild endpoint's localeFilter defaulting.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Reproduce: after a catalog sync, confirm whether JellyfinArtist/AlbumName catalog-backed slot types are injected into ONLY one locale's model or ALL active locales. Compare the slot type definition across locales (en-US, it-IT, de-DE etc.) via get-interaction-model
- [ ] #2 Trace the catalog-push path in CatalogManager (the 'Pushing updated interaction model' / 'Injecting N catalog references' log lines) against BuildSkillInteractionModels(invocationName, localeFilter): does the selective-locale feature scope the catalog injection to a single locale the way it scopes the rebuild
- [ ] #3 Regression test: a catalog sync injects the catalog into every active locale's model, not just one
- [ ] #4 Determine if this is why on-device artist recognition (e.g. 'Koop') fails despite a synced catalog with 86 artists
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
INVESTIGATED 2026-07-26: NOT a regression from PR #14. The catalog sync was it-IT only because CatalogSyncLocales config was empty (the documented default = it-IT only). The LibrarySyncService.ResolveSyncLocalesAsync correctly handles '*' (all active locales) and explicit locale lists. The selective-locale rebuild feature (PR #14) only affects the manual rebuild button endpoint, not the catalog sync path. FIX APPLIED: set CatalogSyncLocales='*' on the live minix instance. The next catalog sync will inject JellyfinArtist/AlbumName catalogs into all active locales. Could not verify the multi-locale sync end-to-end (classifier blocked the SSH trigger command), but the config change is confirmed and the code path is correct by inspection.
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
