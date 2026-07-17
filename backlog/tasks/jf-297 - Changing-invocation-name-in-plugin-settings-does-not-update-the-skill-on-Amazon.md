---
id: JF-297
title: >-
  Changing invocation name in plugin settings does not update the skill on
  Amazon
status: Done
assignee: []
created_date: '2026-06-17 13:07'
updated_date: '2026-06-23 17:06'
labels:
  - bug
  - config
  - smapi
  - alexa
  - tdd
dependencies: []
references:
  - >-
    Controller/ConfigurationController.cs:84-100 (save path — missing deploy),
    :719-765 (RebuildModels — the correct sequence to reuse)
  - >-
    Controller/LWAController.cs:142-148 (re-auth short-circuit that explains the
    user's symptom)
  - 'EntryPoints/SkillStartup.cs:205-216 (startup redeploy triggers)'
  - 'Plugin.cs:184-192 (BuildSkillInteractionModels)'
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/9'
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Controller/ConfigurationController.cs
  - Jellyfin.Plugin.AlexaSkill/Configuration/config.html
  - Jellyfin.Plugin.AlexaSkill.Tests/
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Bug (community user report)

> "to successfully change the invocation name, I had to do it directly in the Alexa project on the Amazon Developer Console; if I tried to update it through the plugin's settings, it didn't work (even after trying to re-authorize)."

The plugin exposes a per-user invocation name field (default `"jellyfin player"`, it-IT `"mia collezione"`) in the config UI. Changing it updates local state but **never propagates to Amazon**, so the spoken wake word stays the old value. The UI silently implies success.

## Root cause (verified against source)

`ConfigurationController.UpdateUserSkill` (`Controller/ConfigurationController.cs:84-100, 213-214`) updates `pluginUser.UserSkill.InvocationName` and calls `SaveConfiguration()` — then returns. **No SMAPI call.** Nothing reaches Amazon.

The other code paths that *could* push it also don't fire on a name change:
- `OnConfigurationChanged` (`Plugin.cs:53-65`) early-returns unless `ServerAddress` changed.
- `SkillStartup` (`EntryPoints/SkillStartup.cs:205-216`) redeploys only on plugin-version change, build failure, or account-linking change.
- LWA **re-authorization** (`Controller/LWAController.cs:142-148`) short-circuits when the skill already exists (`skillExists == true`) — it just refreshes the token and never enters the `else` branch that builds/deploy models. This is exactly why the user's "re-authorize" attempt did nothing.

## What already works (reuse it)

The deploy machinery is correct — only the wiring on the save path is missing:
- `Plugin.BuildSkillInteractionModels(invocationName)` (`Plugin.cs:~184-192`) applies the user's invocation name to all 17 locale models (with it-IT override).
- The manual endpoint `POST /alexaskill/api/custom-model/rebuild` (`ConfigurationController.cs:719-765`) does the full correct sequence: read `UserSkill.InvocationName` → `BuildSkillInteractionModels` → `UpdateSkillAsync` → `PollLocaleBuildStatusAsync` → persist `LastModelDeployTime`/`LastModelDeployStatus`. That same sequence must run when the name changes via the save path.

## Value

The UI field implies the change is live; users waste time re-authorizing (which silently no-ops). Once the fix ships, existing users are repaired retroactively (their stored invocation name finally gets deployed). See `implementationNotes`/plan for the TDD approach.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 When a user with an existing skill (UserSkill.SkillId set + SMAPI device token) changes their invocation name via UpdateUserSkill, the updated interaction models are pushed to Amazon via UpdateSkillAsync using the NEW invocation name
- [ ] #2 When the invocation name is unchanged in a PATCH request, NO redeploy occurs (do not redeploy on every save)
- [ ] #3 When the invocation name changes but the user has no skill yet (empty SkillId) or no SMAPI token, the change is saved locally and NO SMAPI call is attempted (graceful — cannot deploy without a skill)
- [ ] #4 Invalid invocation name (fewer than 2 words) still returns HTTP 400 and does not redeploy
- [ ] #5 Redeploy result is persisted to LastModelDeployTime/LastModelDeployStatus, consistent with the rebuild endpoint
- [ ] #6 Config page surfaces that the change is being applied (redeploying…) and reports success/failure, so the change no longer appears to silently succeed
- [ ] #7 Tests are written FIRST in a failing state, then implementation makes them pass (strict TDD — see plan)
- [ ] #8 Refactor: the duplicated build-models + UpdateSkillAsync + poll sequence is extracted into a single shared helper used by the save path, the rebuild endpoint, and (ideally) the LWA create/reuse path, with all existing tests still passing
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Approach: strict TDD (Red → Green → Refactor)

Write the failing test first. Do NOT write implementation until a test fails for the right reason. One test at a time. Run `dotnet test` (NEVER `--no-build` after code changes — see CLAUDE.md gotcha).

### Phase 0 — Orient (read only, no code)
1. In `Jellyfin.Plugin.AlexaSkill.Tests/`, find how controllers are currently tested (search for existing `ConfigurationController` / `SmapiManagement` test fixtures and the mocking style — Moq? a fake `ISmapiManagement`? a test `Plugin.Instance`?). Match that style exactly. The fix must be unit-testable, which means the SMAPI call must be substitutable in tests.
2. Re-read the two anchor methods: `UpdateUserSkill` (`ConfigurationController.cs:71-215`) and `RebuildModels` (`ConfigurationController.cs:719-765`). The redeploy logic to reuse is the body of `RebuildModels` lines ~736-758.

### Phase 1 — RED: failing tests (write all, confirm each fails for the right reason)
- `UpdateUserSkill_WhenInvocationNameChangesAndSkillExists_RedeploysModelsWithNewName`
  → Setup: user with `UserSkill.SkillId` set + SMAPI token, old invocation name `"jellyfin player"`.
  → Act: PATCH `{ "InvocationName": "media player" }`.
  → Assert: SMAPI `UpdateSkillAsync` was invoked once, with interaction models whose `InvocationName == "media player"`.
  → **Fails today** (no redeploy call).
- `UpdateUserSkill_WhenInvocationNameUnchanged_DoesNotRedeploy`
  → PATCH with the SAME name → `UpdateSkillAsync` NOT called. (Guard against redeploying on every save.)
- `UpdateUserSkill_WhenInvocationNameChangedButNoSkill_SavesWithoutSmapiCall`
  → User with empty `SkillId` → name saved, `UpdateSkillAsync` NOT called, returns success.
- `UpdateUserSkill_WhenInvocationNameInvalid_Returns400`
  → PATCH `{ "InvocationName": "oneword" }` → 400, no SMAPI call.
- Regression: `RebuildModels_*` existing behavior (if any test exists) still passes after extraction.

Run `dotnet test`. Confirm the first test fails because no redeploy happens; the others may already pass (that's fine — they pin behavior).

### Phase 2 — GREEN: make the redeploy test pass (smallest change)
1. Change `UpdateUserSkill` signature to `async Task<ActionResult>` (route stays `[HttpPatch("user-skills/{userId}")]`).
2. Track whether the invocation name actually changed (compare against the value before assignment).
3. After `SaveConfiguration()`, IF the name changed AND `!string.IsNullOrEmpty(pluginUser.UserSkill.SkillId)` AND `pluginUser.SmapiDeviceToken != null`: run the redeploy sequence (mirror `RebuildModels`): `BuildSkillInteractionModels(newName)` → `UpdateSkillAsync(...)` → `PollLocaleBuildStatusAsync(...)` → persist `LastModelDeployTime`/`LastModelDeployStatus` → `SaveConfiguration()`.
4. Keep `IsValidInvocationName` guard (returns 400) BEFORE any deploy — already in place.
5. Run `dotnet test`. First test now passes; others still green.

### Phase 3 — REFACTOR: extract the shared sequence (keep green)
- Extract the build→`UpdateSkillAsync`→poll→persist sequence into one private method, e.g.:
  `private async Task<(bool Success, Dictionary<string,(bool,string,string?)> LocaleResults)> RedeployModelsAsync(Entities.User user, string skillId, string invocationName, CancellationToken ct)`
- Call it from: the new save path (Phase 2), `RebuildModels`, and — if straightforward — the LWA create/reuse `else` branch (`LWAController.cs:172-203`). Stop short if the LWA refactor risks regressions; it's "ideally", not mandatory.
- After each extraction, run `dotnet test` and confirm all green. Do not bundle other changes.

### Phase 4 — UI feedback (acceptance criterion #6)
- In `Configuration/config.html`, the invocation-name save handler should show a "redeploying models…" state while the PATCH is in flight, then surface success / failed-locale count from the response. Match the existing rebuild/deploy status UI patterns already in that file.

### Phase 5 — Live verification (DoD #9, not a unit test)
1. Deploy the DLL to minix (follow the deploy checklist in `.claude.local.md`).
2. DISCOVER the current skill id: `ask smapi list-skills-for-vendor` (NEVER use cached IDs — CLAUDE.md).
3. In the plugin config, change a user's invocation name and save.
4. `ask smapi get-interaction-model --skill-id <ID> --stage development --locale it-IT` → confirm `invocationName` is the new value.
5. Also verify `en-US` (default name) changed. Confirm a 2nd change works (idempotent redeploy).

## Key design decision (resolve before Phase 2)
**Synchronous-await vs fire-and-forget for the redeploy:**
- **Synchronous-await (RECOMMENDED)**: the PATCH blocks ~15-30s while models build+poll, then returns the result. Matches `RebuildModels` exactly, is unit-testable (you can assert the call happened and the returned status), and gives the UI real success/failure feedback. Downside: long-running request.
- **Fire-and-forget** (`Task.Run`, like `LWAController`): PATCH returns instantly, deploy happens in background. Fast UI, but hard to test (timing) and can't report failures to the user.
→ Choose synchronous-await so the TDD tests in Phase 1 are reliable and the UI can report outcome. This is the testability-driven choice.

## Guardrails (from CLAUDE.md / project memory)
- Use `UserSkill.InvocationName` (NOT `User.InvocationName`) — the rebuild endpoint and `BuildSkillInteractionModels` both use the `UserSkill` one. Do not read the wrong field.
- NEVER use cached skill IDs — always `pluginUser.UserSkill.SkillId`.
- Keep `[Authorize(Policy = "RequiresElevation")]` on the endpoint.
- This change does NOT alter interaction-model content (samples/slots), so NLU test fixtures need no update (DoD #6 N/A). The fix only changes WHEN a deploy is triggered.
- Run `dotnet test` without `--no-build` after any source change.
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented + committed (3dc649f) + device-verified. Changing a user's invocation name now triggers an interaction-model redeploy to Amazon via a new IInteractionModelRedeployer seam (build models + UpdateSkillAsync + poll), wired into UpdateUserSkill; the manual rebuild endpoint delegates to the same redeployer. config.html keeps the loading spinner across per-row saves and surfaces the redeploy result. Device test (2026-06-23): changing the name redeploys successfully — 16 locales updated end-to-end. Follow-ups spun out: it-IT is pinned by a separate per-locale override (JF-300); an expired SMAPI access token adds ~refresh latency on the first attempt. Deployed to minix (AlexaSkill_0.8.0.0); config survived.
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
- [ ] #8 Locale response strings added to all 12 locales
- [ ] #9 Invocation name change on a live skill verified end-to-end against SMAPI (not only unit test): change the name in the plugin config, then `ask smapi get-interaction-model --skill-id <ID> --stage development --locale it-IT` and confirm the new invocation name is live
<!-- DOD:END -->
