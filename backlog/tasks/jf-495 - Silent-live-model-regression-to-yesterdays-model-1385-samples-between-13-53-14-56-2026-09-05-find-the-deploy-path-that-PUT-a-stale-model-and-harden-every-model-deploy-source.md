---
id: JF-495
title: >-
  Silent live-model regression to yesterday's model (1385 samples) between
  13:53-14:56 2026-09-05: find the deploy path that PUT a stale model and harden
  every model-deploy source
status: In Progress
assignee: []
created_date: '2026-09-05 13:04'
updated_date: '2026-09-05 16:30'
labels:
  - incident
  - smapi
  - model-deployment
dependencies: []
references:
  - corr=2c86b74b
  - JF-493
  - CatalogManager.UpdateInteractionModelAsync
  - rebuild endpoint locale fallback
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
FORENSIC INCIDENT 2026-09-05: between 13:53 and 14:56 the live it-IT interaction model was silently REPLACED on SMAPI with yesterday's version (59 intents / 1385 samples instead of 60/1402: PlayNextEpisodeIntent and the 10 JF-490 verb-ful che-si-chiama anchors gone). Effect on device: EVERY utterance fell to AMAZON.FallbackIntent (even 'chiedi a mia collezione di suonare i pixies'; requests arrived at the skill as Fallback, corr=2c86b74b/abf83007/410614e6 14:56-14:57). Contained at ~15:05 by redeploying from the working tree (60/1427) via ask smapi set-interaction-model; profile-nlu confirmed routing restored.

EXONERATED: the JF-493 worker (no network calls, timing 12:00-12:45 vs the 13:53-14:56 window, in-process fake SMAPI only); the CatalogSyncTask 12:54 run (its UpdateInteractionModelAsync is GET-live-model -> inject catalog refs -> PUT the SAME model; it cannot replace the intent set, and it ran before the window while 13:51 device tests still worked).

CANDIDATES TO INVESTIGATE (in the plugin's model-deploy code paths):
1. The 16-locale background rebuild loop (12:58-13:15, orchestrator-run): for the 5 INACTIVE locales (hi-IN, ja-JP, nl-NL, pt-BR, ar-SA) the endpoint answered 'Rebuilt 1 locale(s) - 0 succeeded, 0 failed'. Verify what the custom-model/rebuild endpoint does for a locale the skill does not carry: is there a CustomModelLocale DEFAULT fallback (the documented fallback is it-IT!) that rebuilds/RESTORES it-IT from a STORED SNAPSHOT? Where are stored/fetched custom models persisted (CustomModelDeployment 'fetch, deploy, restore' feature) and can a restore path serve a stale (yesterday's 1385) it-IT snapshot?
2. A stored model snapshot path: the 1385 model existed ONLY in yesterday evening's DLL; if the plugin persists fetched models (restore feature) and any path PUTs them, find it.
3. Timing anomaly to explain: device tests at 13:51 worked with 1402; async builds submitted by the loop ended by ~13:15; the regression manifested by 14:56. Check whether an async/queued model PUT from the loop's inactive-locale calls could land late.

REPRO/HARDENING GOALS: identify the exact code path; make every model deploy source-fresh (DLL-embedded or explicit payload, never a stale snapshot); log every set-interaction-model submission with source + intent/sample counts (the 12:54 sync logs 'Fetching/Pushing interaction model' but a silent path would leave no trace: this incident had NONE); consider a post-deploy canary assertion comparing deployed sample counts to expected.

References: corr=2c86b74b, abf83007, 410614e6 (the Fallback storm); containment redeploy 15:05; CatalogManager.UpdateInteractionModelAsync; CustomModelDeployment; Controller rebuild endpoint.
<!-- SECTION:DESCRIPTION:END -->

## Investigation write-up (2026-09-05, code forensics; status stays In Progress)

### Complete inventory of interaction-model PUT sites (verified by grep, no other exists)

| # | Site | Trigger | Content source |
|---|------|---------|----------------|
| 1 | `SmapiManagement.UpdateInteractionModelsAsync` (SmapiManagement.cs, `InteractionModel.Update` in the retry lambda) | skill creation (`CreateSkillAsync`) and every `UpdateSkillAsync` caller | DLL-embedded models from `Plugin.BuildSkillInteractionModels` |
| 1a | `SkillStartup.cs:211` `UpdateSkillAsync` | Jellyfin startup when `cloudVersion != Util.GetVersion()` OR cloud manifest status FAILED | DLL-embedded (the RUNNING DLL's vintage) |
| 1b | `LWAController.cs:190` `UpdateSkillAsync` | LWA re-auth completion on an existing skill | DLL-embedded |
| 1c | `InteractionModelRedeployer.RedeployAsync` | rebuild endpoint (ConfigurationController.cs:901) and invocation-name save (ConfigurationController.cs:282) | DLL-embedded |
| 2 | `ModelDeploymentManager.DeployCustomModelAsync` (`smapi.InteractionModel.Update`) | custom-model/deploy endpoint (URL payload) and custom-model/restore (embedded) | fetched URL JSON or embedded |
| 3 | `CatalogManager.UpdateInteractionModelAsync` (raw `HttpMethod.Put` to `.../interactionModel/locales/{locale}`) | CatalogSyncTask (weekly + startup race window) | GET-modify-PUT of the LIVE model |

### Hypotheses

**H1 (catalog sync GET-race + untracked late build): VERIFIED AS A REAL MECHANISM, most probable content-regression path.**
Before this change `CatalogManager.UpdateInteractionModelAsync` was: GET live model, inject catalog refs, PUT, return on HTTP 202. Two code facts make the incident shape reachable: (a) the GET runs with no check for a pending build, so when another writer (the 12:51 rebuild) submitted a model whose async build had not completed, the GET returns the last-SUCCEEDED model, which that morning was the previous evening's 59/1385 content; the sync then PUT 1385+catalog-refs back; (b) neither the sync nor ANY writer polled its build after the 202, so the regression landed whenever Amazon's per-skill build queue (fed by the ~25 builds submitted 12:51-13:15) got to it, well after the loop visibly "ended". This also AMENDS the task file's exoneration of the 12:54 CatalogSyncTask: "it PUTs the SAME model" is only true when the GET does not race a pending build; that assumption is exactly what broke. The stale-content capability is real even though the sync cannot change the intent set by itself.
Post-hardening: the sync waits for the locale's build to leave IN_PROGRESS before the GET (bounded, best-effort), polls its own update request after the PUT, and canary-verifies the live counts.

**H2 (12:51 rebuild may not have landed; what does RedeployAsync deploy): RESOLVED, with an honest frontier.**
`RedeployAsync` builds from `Plugin.Instance.BuildSkillInteractionModels` (Plugin.cs:211), i.e. the RUNNING DLL's embedded model JSONs plus invocation-name substitution and mood-override injection. It cannot PUT anything older than the DLL it runs from. Whether the 12:51 call PUT 1402 depends entirely on the active DLL's vintage inside the container at 12:51, which code cannot establish. The 13:51 device successes are consistent with BOTH 1385 and 1402 (the tested phrases exist in both), so "1402 was ever live" is unproven either way. Failure modes: `UpdateSkillAsync` PUTs the manifest first and waits on the MANIFEST status only (`WaitForSkillStatusAsync` checks `status.Manifest`, SmapiManagement.cs:283); locale model PUTs are fire-and-forget 202s with 3x retry; `RedeployAsync` then polls locale statuses AFTER all PUTs are submitted.

**H3 (broken-model state via catalog corruption while build reports SUCCEEDED): the corruption modes are REAL and code-reachable; whether one fired is undecidable from code.**
Three verified paths pin a catalog version that may not resolve: (i) `LibrarySyncService.SyncUserLibraryAsync` forwarded STORED catalog ids together with NULL versions whenever that entity type had zero items in the run (e.g. series), and `InjectCatalogReferences` silently pinned `version ?? "1"`; (ii) `UploadCatalogValuesAsync` returns the literal "1" when the version response lacks a Location header or a version field; (iii) nothing detects one catalog id supplied for two slot types. A model referencing a purged or foreign catalog version can build SUCCEEDED yet degrade NLU slot resolution, which is the only code-verified way canonical phrases present in ALL model versions could fall to Fallback. This does not explain the 1385 CONTENT by itself; it is the plausible co-factor for the Fallback storm riding on whichever stale PUT landed.
Post-hardening: the caller forwards an id only with a version minted in the same run; the injection warns on the "1" fallback and on cross-type ids.

**H4 (16-locale loop's inactive-locale calls): no it-IT content side effect; loop behavior explained.**
`BuildSkillInteractionModels` filters embedded locales by name without consulting the skill's active-locale manifest, so for hi-IN/ja-JP/nl-NL/pt-BR/ar-SA the endpoint PUTs the manifest plus that locale's embedded model; SMAPI accepted the PUTs (no exception; 0 update failures), and the poll reported 0 succeeded / 0 failed / empty Locales map because `GetSkillStatusAsync` does not list a locale that has no model status yet (the loop's break condition `results.Count >= status.InteractionModel.Count` exits immediately). Wasteful (a manifest PUT + a possibly-added locale model per inactive locale) but it cannot touch it-IT's model content. REFUTED as the it-IT regression path.

**H5 (other PUT callers): one fully code-verified silent downgrade path found.**
`SkillStartup.cs:205` triggers `UpdateSkillAsync` (manifest + ALL 17 locale models) whenever the cloud manifest's version tag differs from the local DLL version, IN EITHER DIRECTION, or the cloud manifest status is FAILED. A Jellyfin restart under an OLDER active DLL (the versioned-dir displacement documented in CLAUDE.md is a live failure mode) therefore silently redeploys yesterday's models for every locale, with only a generic "Skill for user X is outdated. Updating..." log line and no model-PUT trace. This fits the sharp 13:51-fine / 13:53-broken discontinuity if the container restarted ~13:52 under a stale plugin dir. The invocation-name save path (ConfigurationController.cs:282) and LWA path redeploy current-DLL content only.

### Most probable mechanism (named, with the decision procedure)

Content regression to 1385: EITHER H1 (12:54 sync GET-race, build completing late from a backed-up queue) OR H5 (a ~13:52 Jellyfin restart under an older active DLL firing the startup downgrade). Both are silent-stale-PUT paths of exactly the incident's shape; code alone cannot decide between them. The decision procedure if the incident is revisited: Jellyfin logs around 13:52-13:53 (a startup block, or the "Skill ... is outdated. Updating..." line at SkillStartup.cs:208, means H5; a catalog-sync block at 12:54 followed by nothing means H1's build landed late) and the SMAPI audit history for the skill's it-IT model updateRequests (submission vs completion timestamps). Fallback storm on all-version phrases: not explained by content vintage; the code-verified degradation candidates are the stale/purged catalog version pins (H3 i/ii) and the loss of catalog-backed slot types when a DLL redeploy overwrote an injected model (the embedded models declare static AlbumName/SeriesName seeds; the it-IT embedded model has no JellyfinArtist type at all, so any DLL-origin redeploy reverts the musician slots to the injection-less state until the next sync).

### Contradictions resolved or restated

1. "Device tests at 13:51 worked with 1402" vs "nothing confirms 1402 was ever live": resolved; the 13:51 phrases exist in 1385 too, so they discriminate nothing. 1402-live remains unproven.
2. "Builds ended by ~13:15" vs "storm began 13:53": resolved; submission time is not completion time. SMAPI serializes builds per skill, and a build submitted at 12:54 queued behind ~25 others completes late; alternatively H5 fires instantly at restart. Both consistent with the gap.
3. "CatalogSyncTask exonerated (PUTs the SAME model)" vs H1: restated; the exoneration holds only absent a GET race, which is precisely the unguarded window. The sync is downgraded from "exonerated" to "most probable contributor, race-conditional".

### Evidence frontier (what code cannot answer)

SMAPI's documented GET-during-build semantics (returns last succeeded; asserted from API behavior, not from this codebase), the actual it-IT build completion timestamps that morning, whether Jellyfin restarted ~13:52, and the active DLL's vintage at that moment. All four are answerable from server logs and the SMAPI audit-logs API if this is ever reopened.

## Hardening implemented (JF-495, code + tests only; nothing deployed)

1. **Pre-PUT audit line on EVERY model PUT site** (`InteractionModelPutAudit`, new file `Alexa/InteractionModel/InteractionModelPutAudit.cs`): one grep for `MODEL PUT` now finds every submission, with source (Embedded/CustomUrl/Restore/GetModifyPut), locale, skill, intent and sample counts. Sites wired: `SmapiManagement.UpdateInteractionModelsAsync` (covers skill create, startup update, LWA, redeployer), `ModelDeploymentManager.DeployCustomModelAsync` (+ Restore source via `RestoreDefaultModelAsync`), `CatalogManager.UpdateInteractionModelAsync`.
2. **Ledger recording for catalog-sync PUTs**: `LibrarySyncService` writes a `LocaleModelStatuses` entry (source `CatalogSyncGetModifyPut`) after every model update, with the build status and the canary mismatch text in the Error field.
3. **Post-deploy canary**: `RedeployAsync` (after builds settle, per succeeded locale, via new virtual `SmapiManagement.GetInteractionModelAsync` seam) and `CatalogManager.UpdateInteractionModelAsync` (after polling its update request) GET the live model back and compare intent+sample counts; mismatch logs an ERROR naming both count pairs. Log-only, no rollback, by design. The catalog-sync side tracks the build via the PUT's Location update-request when present and falls back to the skill-status endpoint when it is not (review finding: depending on the Location header alone would have left the canary dead in production on any SMAPI response shape that omits it).
4. **CatalogManager race + build serialization**: the sync waits for the locale's pending build to settle BEFORE the GET (bounded exponential wait, best-effort with warning), and polls its own update request after the PUT, so its build can no longer complete invisibly minutes later.
5. **Stale-version and cross-type warnings**: `InjectCatalogReferences` warns when the "1" fallback engages and when one catalog id feeds two slot types; `UploadCatalogValuesAsync` warns when it returns a literal "1" fallback; `LibrarySyncService` no longer forwards stored ids with null versions at all (leaves the live model's existing catalog reference untouched instead of re-pinning "1").
6. **Loudness fix in ModelDeploymentManager**: the reported build status now reads the locale's interactionModel status (falling back to the manifest's), so a FAILED model build no longer hides behind a SUCCEEDED manifest.

### Verification

- `dotnet build` (plugin + tests): 0 errors, 0 warnings.
- Full suite green: 3282 passed, 0 failed, 0 skipped (baseline 3269 + 13 new: InjectCatalogReferences warnings x2 and UpdateInteractionModelAsync canary/status x6 + ExtractLocaleModelStatus x2 in CatalogManagerTests, redeployer canary x2 in InteractionModelRedeployerTests, ledger x2 + stale-pin guard x1 in LibrarySyncServiceSeriesTests).
- Hardening NOT deployed to SMAPI or the live server (per task rules); the live skill remains the manually-contained 60/1427 state.

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
Formal review dispositions (2026-09-05, orchestrator): P2-85 APPLIED (ModelDeploymentManager now polls the locale's interactionModel build status to terminal under a bounded 30x2s budget via the new WaitForLocaleModelBuildAsync helper; healthy path reports SUCCEEDED again, FAILED derives Success=false, exhausted budget reports TIMEOUT); BT1 APPLIED (HttpRequestException during the update-request poll is caught as an observation failure: buildStatus UNVERIFIED, warning log, locale not marked failed and ledger entry kept); BT2 APPLIED (LocaleModelStatus doc lists TIMEOUT/UNVERIFIED/Skipped). BT3 SKIPPED (coverage-only, behavior unchanged). Coverage note: DeployCustomModelAsync has NO test coverage at any point (pre-existing); pinning the new poll requires making WaitForSkillStatusAsync virtual or a seam change, filed here as the follow-up rather than expanding this stream. Build 0 warnings, suite 3282/3282 after the fixes.
<!-- SECTION:NOTES:END -->
