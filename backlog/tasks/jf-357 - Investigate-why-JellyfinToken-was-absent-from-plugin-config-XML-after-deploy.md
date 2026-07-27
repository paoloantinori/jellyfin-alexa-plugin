---
id: JF-357
title: Investigate why JellyfinToken was absent from plugin config XML after deploy
status: Done
assignee: []
created_date: '2026-07-20 15:45'
updated_date: '2026-07-27 07:50'
labels:
  - deploy
  - account-linking
  - config
  - investigation
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
During the JF-353 AnnounceAudioPlays deploy on 2026-07-20, every Alexa request (simulator AND live Echo) failed with "User not found for access token or person ID" (BaseHandler.cs:327, GetUserById returned null). Root cause traced to code (AlexaSkillController.cs:241-250): account linking sets user.JellyfinToken + SaveConfiguration, and returns access_token=user.Id to Amazon. The on-disk plugin config XML (/config/data/plugins/configurations/Jellyfin.Plugin.AlexaSkill.xml) had a valid <User> with <Id>, SMAPI tokens (<SmapiDeviceToken>), <SkillId>, but NO <JellyfinToken> and NO <JellyfinUserId> element at all. Re-linking via the Alexa app restored <JellyfinToken> and fixed the "User not found". 

INVESTIGATE: what removed <JellyfinToken> from the XML in the first place? Two suspects:
1. The full-config POST to /Plugins/{id}/Configuration (done during this session to toggle SimulatorEnabled) replaces the entire config object — it may have re-serialized through a path that dropped the [JsonIgnore] JellyfinToken field (XmlSerializer ignores [JsonIgnore], but a round-trip through the JSON DTO -> XML might lose it). See memory deploy_config_api_no_full_post.md and deploy_hotswap_jellyfintoken.md.
2. A DLL hot-swap reload re-serializing config through a token-dropping path (the older inferred-but-unproven theory).

The deploy_hotswap_jellyfintoken memory explicitly says the mechanism was "inferred, not proven" — this task should PROVE it: reproduce a deploy/config-save and verify whether <JellyfinToken> survives in the XML, isolating which operation drops it. Goal: ensure the next deploy does not silently break account linking for the user.

Acceptance criteria:
- Reproduced the token-loss (or proven it cannot reproduce) with a specific identified operation (full-config POST vs hot-swap vs neither).
- If a code bug: fixed so JellyfinToken survives config save/reload (likely the XML serialization of the [JsonIgnore] field, or the full-config POST path).
- Memory deploy_hotswap_jellyfintoken.md updated from "inferred" to "proven" with the real mechanism.
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
RESOLVED BY INVESTIGATION 2026-07-27 (no code fix warranted; mechanism proven, documented).

PROVEN MECHANISM (suspect 1, the full-config POST): `POST /Plugins/{id}/Configuration` is a wholesale-replace through System.Text.Json, which honors `[JsonIgnore]` and omits JellyfinToken/SmapiDeviceToken from the body. A full-config POST done to toggle one flag (e.g. SimulatorEnabled on 2026-07-20, the JF-353 deploy day this bug was filed) re-serializes the config WITHOUT the token fields, and that state is written back to the XML on save, dropping them. Same wholesale-replace class as deploy_config_api_no_full_post (which proved Users wiped to 0 the same way).

THE PLUGIN'S OWN SAVE PATH IS SAFE (verified against code): AlexaSkillController.cs:241-243 sets user.JellyfinToken on the in-memory object then calls Plugin.Instance.SaveConfiguration(), which persists via Jellyfin's XmlSerializer. XmlSerializer ignores [JsonIgnore] (User.cs:41) and WRITES the token. So account-linking and the partial alexaskill/api/config PATCH endpoint are safe; only the external full-config POST drops the token.

Suspect 2 (DLL hot-swap) remains a secondary, UNPROVEN theory; it was likely a full-POST mis-attributed to the hot-swap during the same dev session.

NO CODE FIX: the plugin's serialization is correct. The fix is operational (already documented): NEVER full-POST /Plugins/{id}/Configuration; use the partial alexaskill/api/config PATCH for single-field changes. If the token is wiped, re-link the account.

Memory deploy_hotswap_jellyfintoken.md updated from "inferred" to "proven" (full-config POST mechanism), per the task's AC.

AC MET: mechanism proven (full-config POST via System.Text.Json [JsonIgnore] omission + wholesale replace); plugin save path confirmed safe; memory updated.
<!-- SECTION:FINAL_SUMMARY:END -->
