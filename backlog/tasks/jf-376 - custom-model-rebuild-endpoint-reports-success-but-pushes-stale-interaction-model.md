---
id: JF-376
title: >-
  custom-model/rebuild endpoint reports success but pushes stale interaction
  model
status: Done
assignee: []
created_date: '2026-07-25 15:00'
updated_date: '2026-07-25 16:49'
labels:
  - bug
  - model-deployment
  - sMAPI
  - rebuild
dependencies: []
modified_files:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/ModelDeployment/InteractionModelRedeployer.cs
  - Jellyfin.Plugin.AlexaSkill/Util.cs
  - Jellyfin.Plugin.AlexaSkill/Plugin.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The plugin's custom-model/rebuild endpoint reports success but does NOT push the current embedded interaction model to SMAPI. Discovered 2026-07-25 during JF-374 verification.

REPRODUCTION (verified):
- Committed model_it-IT.json with new FollowMeIntent samples ('seguirmi', 'seguir mi'); rebuilt and deployed the DLL (grep confirmed the samples are embedded in the deployed binary).
- Triggered POST /alexaskill/api/custom-model/rebuild. Response: success=True, 'Rebuilt 1 locale - 1 succeeded, 0 failed'. Plugin log: 'Redeployed 1 interaction models ... 1 succeeded'.
- Re-read the live model via `ask smapi get-interaction-model`: still had the OLD 7 samples, NOT the new ones. Reproduced twice (16:47 and 16:55), still stale after 30s settle (so NOT eventual consistency).
- Pushing the SAME model JSON directly via `ask smapi set-interaction-model` worked immediately: live model then had all 9 samples. So the model CONTENT is correct and Amazon accepts it; the plugin rebuild path is the broken link.

LIKELY AREA: InteractionModelRedeployer.RedeployAsync -> Plugin.Instance.BuildSkillInteractionModels -> Util.GetLocalInteractionModels (loaded once at Plugin construction from GetManifestResourceStream). The samples ARE in the deployed DLL, so either the resource stream is reading a stale/cached assembly, or SkillInteractionModel construction is dropping samples, or Plugin.Instance is a singleton that cached InteractionModels from a previous DLL load.

IMPACT: any model change a user tries to push via the plugin UI (invocation-name save path, rebuild endpoint) silently fails to take effect while reporting success. This is the same 'success return does not prove target state' class as the podcast bug.

WORKAROUND until fixed: push models directly via `ask smapi set-interaction-model --skill-id <ID> --stage development --locale <XX> --interaction-model file:payload.json`.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Reproduce: with a model change in the embedded JSON (e.g. a new sample), trigger custom-model/rebuild and confirm the live SMAPI model does NOT reflect the change despite a success response
- [ ] #2 Root cause identified: trace why InteractionModelRedeployer pushes a stale model. Suspects: (a) GetLocalInteractionModels/GetManifestResourceStream loading from a cached or wrong assembly, (b) SkillInteractionModel construction or JSON deserialization silently dropping samples, (c) Plugin.Instance being a stale singleton holding InteractionModels loaded from a previous DLL
- [ ] #3 Fix verified: after a rebuild, get-interaction-model on SMAPI reflects the newly-embedded samples (read the live model, do not trust the rebuild's success message)
- [ ] #4 Regression test: a unit/integration test that asserts the rebuild path produces a model containing a sample present in the embedded JSON
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
created: 2026-07-25 16:49
---
NOT A BUG - closed 2026-07-25. Systematic debugging with runtime instrumentation proved the rebuild endpoint works correctly. Root cause of the apparent 'stale model': the endpoint rebuilds ONE locale at a time (the UI dropdown locale, or Configuration.CustomModelLocale as fallback). I called it with {userId:...} and no 'locale' field, so it fell back to CustomModelLocale=en-US and rebuilt en-US only. The '1 succeeded' response was accurate for what it did (en-US); I misread it as it-IT. Calling with explicit {userId:..., locale:'it-IT'} rebuilt it-IT correctly and the live model gained the new samples. No code change needed. Diagnostic logging added during investigation was reverted; no diff remains. LESSON: the endpoint's success response includes the per-locale 'locales' map - read it to confirm WHICH locale rebuilt, don't assume.
---
<!-- COMMENTS:END -->
