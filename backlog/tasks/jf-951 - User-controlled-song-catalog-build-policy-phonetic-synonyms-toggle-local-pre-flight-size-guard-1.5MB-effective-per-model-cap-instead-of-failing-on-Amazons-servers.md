---
id: JF-951
title: >-
  User-controlled song-catalog build policy: phonetic-synonyms toggle + local
  pre-flight size guard (1.5MB effective per-model cap) instead of failing on
  Amazon's servers
status: Done
assignee: []
created_date: '2026-11-23 15:30'
updated_date: '2026-11-25 00:14'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
gate-exempt: /simplify on the review-round delta only (the seven 17b34fe2 fixes are review-driven: two surface wirings, a ledger-semantics rework, a byte-accounting fix, a parse removal, wording, named args); the worker's own 4-agent /simplify covered the base 45598deb; the /code-review evidence (worker high + orchestrator medium, all findings applied) is in this transcript.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped 2026-11-25 via the merge of worktree-agent-a9172ec247a3d4222 (base 45598deb + review round 17b34fe2). User.CatalogSongSynonymsEnabled (default false = the shipped values-only) with the FULL user surface: a ConfigurationController handler (null/absent leaves untouched) and a config.html checkbox beside CatalogSongSyncEnabled (embedded-resource checklist honored: DLLs deleted before the Release rebuild, the new string verified in both Release DLLs; the served-page curl-grep stays on the deploy checklist). The pre-flight guard in UpdateInteractionModelAsync: EstimateEffectiveModelSizeBytes (one pure function; constants 1,500,000 cap / 1,450,000 safety line), the wired set from the injection's own single-parse report (declare-skipped types contribute zero; a stale wired block counts its current bytes, closing the zero-count window), PUT skipped on exceed keeping the previous good model, SyncResult.SkippedOversizeLocales, one Warning per skipped locale naming the measured numbers, the threshold and the cap as separate facts, and the levers. Ledger semantics per the review: a skip PRESERVES the SUCCEEDED row and carries a durable OversizeSkipped caveat bit rendered on the config page (ModelsDeployed cannot false-negative); the deliberate deviation is documented: no Jellyfin UI notification because INotificationManager is absent from both reference-assembly TFMs (verified). 6318/6318 both TFMs; merged main builds clean. Review round: 7 findings, all applied (toggle surface, ledger semantics, persistent signal, stale-block bytes, single parse, honest wording, named optionals). Gates: worker /simplify + /code-review high (8 findings incl. the declare-check over-count fix) on the base; orchestrator /code-review medium (7 findings) on the delta. gate-exempt: /simplify on the review delta (review-driven fixes); the base's own gates ran in the worker's session.
<!-- SECTION:FINAL_SUMMARY:END -->
