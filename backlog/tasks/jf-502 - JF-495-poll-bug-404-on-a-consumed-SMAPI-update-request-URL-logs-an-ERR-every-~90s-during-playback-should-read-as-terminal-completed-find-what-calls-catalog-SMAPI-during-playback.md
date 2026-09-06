---
id: JF-502
title: >-
  JF-495 poll bug: 404 on a consumed SMAPI update-request URL logs an ERR every
  ~90s during playback (should read as terminal-completed); find what calls
  catalog SMAPI during playback
status: In Progress
assignee: []
created_date: '2026-09-06 08:47'
updated_date: '2026-09-06 11:22'
labels:
  - smapi
  - bug
  - catalog
dependencies: []
references:
  - JF-495
  - 'podman logs 2026-09-06 10:43-10:46'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Bug in the JF-495 hardening observed live 2026-09-06 (10:43-10:46, during episode playback): CatalogManager logs 'SMAPI request failed: 404 Not Found' with an HTML body roughly every 90 seconds. The update-request Location URL used by PollSmapiOperationAsync appears to be consumed/expired after the build completes (or is one-shot), so a poll that outlives it errors instead of reading as terminal. Fix: in PollSmapiOperationAsync (and the model-build settle wait), treat 404 on the update-request URL as TERMINAL-COMPLETED (or at minimum downgrade to a debug log with a terminal disposition); also identify WHAT issues catalog-manager SMAPI calls every ~90s during playback (dynamic entities? a periodic refresh?) since no catalog sync was scheduled in that window, and make sure path does not hammer SMAPI. Evidence: podman logs 10:43:48, 10:45:08, 10:46:29 2026-09-06.
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
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation notes (2026-09-06)

Items 4 to 8 are not applicable to this change: no session attributes, no HttpClient lifecycle change, no interaction-model/locale-string change (the catalog-manager fix is SMAPI error disposition only), and no on-device E2E is possible for it without calling SMAPI (explicitly out of scope for this task; the 404 paths are covered by unit tests with fake handlers).

### Fix 1: poll 404 reads as terminal-completed

`Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/CatalogManager.cs:1055-1071` (`PollSmapiOperationAsync`): a poll response with status 404 on the update-request Location URL now logs one Debug line ("update request consumed or expired after completion; treating as terminal-completed with an unknown version") and returns null instead of reaching `EnsureSuccessAsync`, which logged the ERR and threw `HttpRequestException`. Callers: `UploadCatalogValuesAsync` (null version takes the existing JF-495 warning + `"1"` fallback), `CreateSlotTypeVersionAsync` (no return value, success), `UpdateInteractionModelAsync` (poll returns without throwing, so the disposition becomes SUCCEEDED and the post-build canary still verifies the live model). Any other HTTP failure (429, 5xx) keeps the current transient behavior (ERR + throw), locked by `UploadCatalogValuesAsync_PollReturns503_StillSurfacesError`.

### Fix 2: the ACTUAL source of the live ERRs, the settle-wait status GET

Live evidence shows the observed ERRs were NOT on an update-request Location URL. Every one of the 12 ERRs in the 10:35-10:51 window has the stack `EnsureSuccessAsync` called from `TryGetLocaleModelStatusAsync`, i.e. the GET on `https://api.amazonalexa.com/v1/skills/{skillId}/stages/{stage}/status` (the settle-wait URL built at `CatalogManager.cs:629`) returned 404 with an HTML body, once per locale. `CatalogManager.cs:637-647` (`TryGetLocaleModelStatusAsync`): a 404 there now logs one Debug line ("no readable status; treating as no reported status") and returns null, the same best-effort outcome the existing catch below already produced (skip the settle wait), minus the ERR. Behavior is otherwise unchanged.

### Caller identification: the startup catalog sync itself; playback is coincidental

- `CatalogManager` has exactly one consumer: `LibrarySyncService` (DI singleton registration at `EntryPoints/Registrator.cs:80`). `LibrarySyncService.SyncUserLibraryAsync` is invoked from exactly one place: `EntryPoints/CatalogSyncTask.cs:105`, whose triggers are `StartupTrigger` + weekly `IntervalTrigger` (`CatalogSyncTask.cs:140-145`).
- Nothing in the playback path performs SMAPI HTTP: `Alexa/DynamicEntities/` and `Alexa/Playback/` contain no HttpClient usage at all (dynamic entities ship as a response directive, not a SMAPI call); the remaining `api.amazonalexa.com` callers (SmapiManagement via SkillStartup/LWAController/ConfigurationController/InteractionModelRedeployer, ProactiveEventClient, reminders) are startup, config-save, and per-request Alexa-API paths, none on a playback cadence.
- Live log confirmation (podman logs, 2026-09-06): the 12 ERRs run 10:35:43 to 10:51, one per locale iteration of the startup catalog sync (12 locales, "Catalog sync locale ... completed" x12; e.g. "en-GB completed in 78523ms"). The ~80s cadence is the per-locale duration (3 catalog uploads + model GET-modify-PUT + a ~50s poll), not a playback-driven poller. The cited 10:43:48 ERR is locale en-US, mid-sync, while playback happened to be running.
- Conclusion per the task instructions: the caller is the sync itself, so the fix is complete with the 404 dispositions above. No playback-cadence caller exists; no hammering path to bound.

### Factual correction to the task premise (live log evidence)

- The model PUT's Location header in this deployment is `.../v1/skills/{skillId}/status?resource=interactionModel` (a skill-status URL), not an updateRequest URL. Its polls return 200 with a shape `ExtractPollStatus` cannot parse, so the poll logs `status=null` for all 30 iterations and ends in TimeoutException, every locale (12/12 model polls timed out in the window). No poll 404 occurred in the window; the catalog version updateRequest polls (3 per locale) all resolved IN_PROGRESS to SUCCEEDED normally. The poll-404 fix (Fix 1) is still correct for the documented SMAPI consumption semantics of updateRequest resources and is exercised only when Amazon actually 404s a poll.

### Scoped follow-ups (found during this investigation, deliberately NOT fixed here)

1. The settle-wait status URL `GET /v1/skills/{skillId}/stages/{stage}/status` answered 404 for 12/12 locales (0 successes; zero "Waiting for pending interaction model build" lines in the window). The SDK-backed status call elsewhere (`SmapiManagement.GetSkillStatusAsync` at `Alexa/SmapiManagement.cs`, `Skills.Status(skillId)`) uses `GET /v1/skills/{skillId}/status` without the stages segment. The settle wait is therefore a no-op in practice. Fixing the URL (or routing through the SDK) is beyond the 404 disposition and needs its own verification.
2. The model PUT build is never observed: `PollSmapiOperationAsync` polls the Location with `ExtractPollStatus`, which cannot parse the skill-status shape (`interactionModel.{locale}.lastUpdateRequest.status`). Every locale burns the full ~50s poll budget, then lands buildStatus TIMEOUT ("did not settle within the poll budget") and the canary never runs. `WaitForModelBuildOutcomeViaSkillStatusAsync` already parses that shape correctly via `ExtractLocaleModelStatus`; routing the model PUT's post-PUT wait through it (or teaching the poll the locale-nested shape) would make every locale ~50s faster and turn TIMEOUTs into verified SUCCEEDED/FAILED + canary. Both follow-ups interact with the same lines fixed here, so they should land in a task that re-reads this file after this change.

### Verification (2026-09-06)

- `dotnet build Jellyfin.Plugin.AlexaSkill.sln`: Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test Jellyfin.Plugin.AlexaSkill.Tests`: Passed! Failed: 0, Passed: 3340, Skipped: 0, Total: 3340 (baseline 3336 + 4 new: poll-404 terminal, settle-404 quiet, catalog poll-404 fallback version, poll-503 still errors).
- /simplify pass applied (test verify helpers deduplicated; `NotFoundHtml` accessibility kept `internal` because the outer test class cannot reach nested private members). code-review (review-local methodology, high): no findings at or above the 80 threshold; two sub-threshold notes recorded (the `"1"` fallback delta on a catalog poll 404 is the instructed disposition and already warns via the JF-495 path; treating any poll-URL 404 as terminal is bounded by the Location always originating from SMAPI's own 202 response).
- Log evidence gathered read-only from the minix container (`podman logs jellyfin`); no deploy, no SMAPI calls, no model files touched.
