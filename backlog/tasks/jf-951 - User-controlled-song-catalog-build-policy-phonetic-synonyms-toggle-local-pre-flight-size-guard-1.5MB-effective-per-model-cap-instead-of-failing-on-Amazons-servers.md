---
id: JF-951
title: >-
  User-controlled song-catalog build policy: phonetic-synonyms toggle + local
  pre-flight size guard (1.5MB effective per-model cap) instead of failing on
  Amazon's servers
status: To Do
assignee: []
created_date: '2026-11-23 15:30'
labels:
  - catalog
  - nlu
  - jF-946-followup
  - ux
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/LibrarySyncService.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/CatalogManager.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Maintainer design (2026-11-23, after the live ExceededMaximumModelSize findings): the song-catalog build policy becomes USER-CONTROLLED with a LOCAL pre-flight size guard. Background: Amazon's per-locale interaction-model cap is 1.5MB EFFECTIVE (serialized model + the bytes of every catalog wired into it; measured live: our 9,458-title library with per-locale phonetic song synonyms = it-IT 2.0MB, de-DE 1.6MB, ja-JP 2.5MB with the inert block; values-only fits with headroom). The limit is PER-MODEL, not the sum across locales, but a large monolingual library still exceeds it with synonyms, while a small library fits comfortably: the right policy is per-user choice guarded by measurement, not a global default.
SCOPE: (1) Per-user toggle 'Song title phonetic synonyms' (default OFF = the shipped values-only; persisted like CatalogSongSyncEnabled, config UI beside it). (2) PRE-FLIGHT SIZE GUARD in the sync: before each locale's model PUT, compute the effective size = serialized model JSON (post-injection) + the serialized byte length of every catalog payload wired into that locale (payloads already in memory at sync time); compare against the 1.5MB cap with a safety margin (say 1.45MB). On exceed: SKIP the PUT for that locale (keep the previous good model), log a LOUD warning naming the numbers and the lever ('disable song phonetic synonyms' or 'fewer titles'), and surface the skipped locales in the SyncResult so the config page can tell the user their chosen combination is incompatible. (3) The declare-only Song injection (OnlyReplaceExisting, shipped with the immediate fix) stays: non-declaring locales never carry the song bytes. (4) Tests: estimator unit tests over known payload sizes; guard test (over-cap payload -> PUT skipped, warning logged, result carries the locale); toggle test (synonyms on/off flow through SongTitlePreparation.SongSynonyms).
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
