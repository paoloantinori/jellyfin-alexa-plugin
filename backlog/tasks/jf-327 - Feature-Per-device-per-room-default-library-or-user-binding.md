---
id: JF-327
title: 'Feature: Per-device / per-room default library or user binding'
status: Done
assignee: []
created_date: '2026-07-12 15:00'
updated_date: '2026-09-19 06:43'
labels:
  - feature
  - multi-user
  - config
milestone: m-10
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs
  - Jellyfin.Plugin.AlexaSkill/Configuration/config.html
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Multi-user settings are keyed per Jellyfin user and queues are per-Echo-device (DeviceQueueManager), but there is no way to say "this kitchen Echo defaults to the Kids library / Dad's account" (functional review 2026-07-12). High value for households: a shared Echo in a common room, or a child's room Echo that should only reach kid-safe content.

The `deviceId` is already captured on requests. Add a device→(user and/or library) mapping in plugin config with a config-UI section, and resolve the effective user/library from the device binding when present (falling back to the current account-linking behavior). Combines naturally with existing per-user library gating (FilterByContentAccess/ApplyLibraryFilter) and voice-profile identification. Include config DTO fields, config.html UI, resolution logic in the request pipeline, and tests.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 An admin can bind a specific Echo deviceId to a default Jellyfin user and/or a default library scope in the config UI
- [x] #2 When a bound device makes a request, the effective user/library is resolved from the binding
- [x] #3 Unbound devices retain the current account-linking behavior (no regression)
- [x] #4 Per-user content/library gating is applied on top of the device binding
- [x] #5 Config persists correctly without wiping other settings (uses a dedicated endpoint, not full config overwrite)
- [x] #6 Unit tests cover binding resolution and fallback
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-19 ~02:05 feasibility scan (night run; implementation NOT started - design questions for the morning first). INTEGRATION ANCHOR: AlexaSkillController.cs ~line 320-350 already extracts deviceId (req.Context.System.Device?.DeviceID) and resolves the user via Configuration.GetUserById(userId from the LWA access-token GUID), with a PersonId branch for voice profiles just above. A DeviceBinding lookup slots exactly between those: config.DeviceBindings (Collection<DeviceBinding> XmlSerializer-safe, {DeviceId, BoundUserId?, BoundLibraryId?}) resolved after user resolution, before handler dispatch; the library half rides the existing per-user library gating (ApplyLibraryFilter already takes the resolved Entities.User). DESIGN QUESTIONS FOR PAOLO (these gate implementation): (1) per-user skills nuance - each Jellyfin user has their OWN skill, so within one skill's requests the speaker is already the linked user; the USER-binding half of the feature is mostly relevant for the skill OWNER delegating a shared Echo to another user's CONTENT, which in practice means the LIBRARY binding (BoundLibraryId) is the real feature; do we ship library-only binding V1? (2) precedence - voice profile (PersonId) should presumably WIN over the device default (it identifies the actual speaker); device binding wins over plain linked-account fallback. Confirm. (3) queue ownership: DeviceQueueManager keys queues by deviceId already; if a bound device serves a different user's content, whose DeviceQueue/LastPlayed ledger applies (tonight's JF-566-588 reliability layer keys playback state by device - the binding must not silently mix ledgers). (4) AC#5 dedicated endpoint: follow the alexaskill/api/config PATCH flat-field pattern (NOT updatePluginConfiguration, which wipes Users). UI: config.html new accordion + the devices list can seed from Jellyfin's /Devices endpoint for display names. Estimated implementation after decisions: config DTO + PATCH field + controller resolution + config.html section + 6-8 unit tests; no interaction-model or locale changes (no new intents).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped V1 (library-only, per Paolo's morning decisions: profile > binding > linking), deployed and verified. Config: DeviceLibraryBindings Collection (XmlSerializer-safe) + DeviceLibraryBinding DTO + case-insensitive lookup; config.html "Device Library Bindings" accordion with a live library dropdown (fetchLibraries reuse), add/remove rows, saved through the partial config PATCH (never updatePluginConfiguration). Resolution at BaseHandler.HandleRequestAsync - the one funnel covering handlers AND the simulator; recognized voice profile skips the binding; the binding INTERSECTS the user's AllowedLibraryIds (never grants; an impossible Guid expresses nothing-accessible since an empty list means ALL); unbound devices byte-identical (Same-instance pin); config-owned user never mutated (reflection clone, request-scoped). Re-fetch sites re-scoped too: ResumeIntentHandler, radio + PostPlay AutoPlay events (device-only overload; events carry no person), DynamicEntitiesInterceptor values. HONEST REVIEW TRAIL: the first integration placed Apply in the controller and was DEAD WIRING (pipeline drops the user; handlers re-resolve from config) - caught by the combined simplify+code-review dispatch, exactly the handler-routing failure class; fixed by moving to the funnel and adding a funnel test (real-Plugin construction, PipelineTests recipe) that is red on the dead version. Suite 4145/4145 both TFMs (+9); Release 0 warnings. LIVE: deployed, served page carries the accordion (cache-buster grep), PATCH round-trip green (binding set -> read back with all four fields -> cleaned to empty). Residual for on-device: bind a real Echo id (from the plugin log DeviceId scope) and listen; V2 candidates noted in the task: per-device USER binding (needs the queue-ledger design question answered), device dropdown seeded from a device inventory if one ever becomes enumerable.
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
