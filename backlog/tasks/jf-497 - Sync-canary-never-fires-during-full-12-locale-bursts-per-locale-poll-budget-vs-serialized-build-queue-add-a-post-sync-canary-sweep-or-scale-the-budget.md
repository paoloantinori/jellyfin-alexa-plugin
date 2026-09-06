---
id: JF-497
title: >-
  Sync canary never fires during full 12-locale bursts (per-locale poll budget
  vs serialized build queue): add a post-sync canary sweep or scale the budget
status: In Progress
assignee: []
created_date: '2026-09-05 16:44'
updated_date: '2026-09-06 09:53'
labels:
  - smapi
  - catalog-sync
  - observability
dependencies: []
references:
  - JF-495
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live observation from the JF-495 post-deploy verification (2026-09-05 18:32-18:43): the hardened catalog sync serializes per-locale build waits correctly (the H1 GET-race is gone) and all 12 builds eventually SUCCEEDED with correct content, but during a FULL 12-locale sync burst every locale's own build settles AFTER its ~80s poll budget expires (SMAPI serializes builds per skill: locale N waits for N-1 predecessors), so every locale logs 'did not settle within the poll budget' (buildStatus TIMEOUT) and the post-deploy CANARY never fires during bursts. The canary currently only fires for single-locale operations (rebuild endpoint, custom deploys). Options: scale the per-locale poll budget by remaining queue position, or run a post-SYNC canary sweep (after the last locale's PUT, GET-back all locales once the queue drains) instead of per-locale mid-sync. Not urgent: content correctness is preserved by the serialization; this is about making the canary actually observe burst deployments.
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
ROOT CAUSE FOUND (JF-502 worker, 2026-09-06): the burst TIMEOUTs are NOT a poll-budget problem. The model PUT's post-PUT poll receives a Location whose shape ExtractPollStatus cannot parse (a skill-status URL, not an update-request URL), so the poll can never resolve: every locale burns its full ~50s budget and lands TIMEOUT, and the canary never fires in any sync (single-locale rebuilds use the redeployer's poll, which works). The cure is to parse/redirect that Location through WaitForModelBuildOutcomeViaSkillStatusAsync (already parses the shape) or fall back to it immediately when the Location does not match the update-request pattern; expect the sync to drop from ~16 minutes to ~2-4 and the canary to fire per locale. Second scoped item from the same worker: the settle-wait status GET uses /v1/skills/{id}/stages/development/status which 404s 12/12 (the SDK uses /v1/skills/{id}/status); it is a silent no-op today - fix the path while in there. Original options (post-sync canary sweep / budget scaling) are superseded by the poll-shape fix.

FIXED (2026-09-06, worker session): root-cause fix implemented, superseding the budget-scaling / canary-sweep options.

1. Location-shape dispatch in CatalogManager.UpdateInteractionModelAsync (model-PUT path, ~line 492): the resolved Location is checked by new IsUpdateRequestLocation (an 'updateRequest' path segment, the only shape ExtractPollStatus parses). Genuine update-request Locations keep PollSmapiOperationAsync; anything else (the live-observed skill-status URL) falls back IMMEDIATELY to WaitForModelBuildOutcomeViaSkillStatusAsync with an Information log, so no budget is burned on an unparseable URL.

2. Settle-wait status GET fixed: TryGetLocaleModelStatusAsync now calls SkillStatusUrl(skillId) = /v1/skills/{id}/status (single owner of the string, shared by settle wait and fallback tracker). The old /v1/skills/{id}/stages/{stage}/status 404'd 12/12 live; the non-staged shape verified against Alexa.NET.Management 5.10.0 (Skills.Status string table) and the redeployer's working GetSkillStatusAsync path. Dead 'stage' parameter dropped from the three status helpers.

3. Tests (fake handler): a model PUT whose Location is a skill-status URL resolves via the status endpoint in the first poll iterations (BuildStatus SUCCEEDED, canary fires exactly once, zero update-request polls, no TIMEOUT); genuine update-request Locations still poll the operation endpoint; the settle-wait GET asserts the non-staged URL shape. 5 new tests in CatalogManagerTests (UpdateInteractionModelAsync_SkillStatusLocation_FallsBackImmediately_CanaryFires, _GenuineUpdateRequestLocation_PollsOperationEndpoint, _SettleWait_UsesNonStagedSkillStatusUrl, IsUpdateRequestLocation x2); ModelPutFakeHandler gained a PutLocation enum + request recording; LibrarySyncServiceSeriesTests fake route updated to /status.

Verification: dotnet build 0 errors 0 warnings; dotnet test full suite 3345/3345 green. Gates: /simplify ran (unified the duplicated fallback call; reuse + efficiency clean), review-local ran inline (no findings >= 80; one doc-drift finding applied: UpdateInteractionModelAsync summary now names the Location-shape dispatch). Expected live effect per the root-cause note: sync ~16 min -> ~2-4 min, canary fires per locale. Status stays In Progress pending live post-deploy confirmation.

[code-review gate 2026-09-06, sub-threshold observations recorded per the same-turn landing rule; both assessed BELOW the 80 reporting threshold, no action required now] (1) Canary blindness on same-count failures: in the sync path the PUT is a GET-modify-PUT whose intent/sample counts equal the live model's by construction (catalog injection touches only the types array), so a build that silently never landed cannot be caught by the counts-only canary; the fallback tracker's 500ms pre-delay plus the i==0 'may reflect the previous build' log are the only guards. Acceptable because consequence is bounded (next weekly sync re-submits; next locale's settle wait observes the in-flight build). Revisit only if the ledger ever shows SUCCEEDED+canary-OK while the live model lacks the injected types. (2) Status-404 asymmetry (JF-502/JF-497 cross-path): on the update-request poll a 404 reads as terminal-completed, but on the skill-status fallback a 404 reads as null and WaitForModelBuildOutcomeViaSkillStatusAsync keeps polling to a ~57s TIMEOUT. Defensible (status-404 = no status object, not operation-gone) and loud rather than silent, but if Amazon ever 404s the bare /v1/skills/{id}/status shape, every locale burns the fallback budget and records TIMEOUT; the settle-wait path stays cheap (null returns immediately).
<!-- SECTION:NOTES:END -->
