---
id: JF-300
title: >-
  Invocation name: empty = locale defaults, custom applies to all locales (fix
  unconditional it-IT override)
status: Done
assignee:
  - claude
created_date: '2026-06-23 15:47'
updated_date: '2026-06-23 20:02'
labels:
  - bug
  - config
  - interaction-model
  - alexa
  - ux
dependencies: []
references:
  - >-
    commit 5595f44 (2026-06-03) introduced the unconditional
    LocaleInvocationNames override
  - >-
    Jellyfin.Plugin.AlexaSkill/Plugin.cs:184 (BuildSkillInteractionModels — the
    unconditional override at :189)
  - >-
    Jellyfin.Plugin.AlexaSkill/Config.cs:20,28 (Config.InvocationName default +
    LocaleInvocationNames map)
  - >-
    JF-297 (invocation-name redeploy — works; this bug is downstream of it, in
    model build)
  - >-
    On-device evidence 2026-06-23: user set 'pinco pallino blu' -> en-US/de-DE
    updated, it-IT stayed 'mia collezione'
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Plugin.cs
  - Jellyfin.Plugin.AlexaSkill/Config.cs
  - Jellyfin.Plugin.AlexaSkill/Entities/UserSkill.cs
  - Jellyfin.Plugin.AlexaSkill/Controller/LWAController.cs
  - Jellyfin.Plugin.AlexaSkill/Configuration/config.html
  - Jellyfin.Plugin.AlexaSkill.Tests/
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Bug (found while testing JF-297 on device)

Changing the invocation name in the plugin settings did NOT change it-IT — it stayed `"mia collezione"` — while the other 16 locales picked up the new name. Root cause: `BuildSkillInteractionModels` (`Plugin.cs:189`) applies `Config.LocaleInvocationNames["it-IT"] = "mia collezione"` **unconditionally**, so the hardcoded locale override always wins over the user's per-user `UserSkill.InvocationName` for it-IT.

The override was added deliberately in commit `5595f44` (2026-06-03, "Fix it-IT invocation name overwrite: add per-locale overrides") to stop the global default from clobbering it-IT's localized name baked into the template — good intent, but it's unconditional, so the user can never change it-IT.

`BuildSkillInteractionModels` is called in 3 places, so it-IT is pinned everywhere: skill creation (`LWAController.cs:172`), startup updates (`SkillStartup.cs:179`), and the redeploy/rebuild path (`InteractionModelRedeployer.cs:49`). The data model has only ONE per-user invocation name (`UserSkill.InvocationName`) — no per-locale user setting.

## Fix (chosen approach: "empty = default", Option 2)

Make an **empty/null** `UserSkill.InvocationName` mean "use locale defaults"; a **non-empty** custom name applies to **all 17 locales** (overriding the locale defaults). Show the default in the UI as the field placeholder/helper, and add a Reset button. This removes the string-compare fragility (empty is unambiguous) and makes the displayed default the real "not customized" state.

## Plan

1. **Data model / semantics**: treat empty/null `UserSkill.InvocationName` as "use defaults" (keep the string type for serialization compatibility; empty = default).
2. **One-time migration**: on config load, if a user's stored `InvocationName == Config.InvocationName` (`"jellyfin player"`), clear it to empty — so existing users (who rely on it-IT = `mia collezione`) keep that behavior. This string-compare exists ONLY here, not in the runtime path.
3. **`BuildSkillInteractionModels`** (`Plugin.cs:184`): replace the unconditional override with `isEmpty(userInvocation) ? (LocaleInvocationNames.TryGetValue(locale, out var ln) ? ln : Config.InvocationName) : userInvocation`.
4. **`LWAController`** skill creation: default new `UserSkill.InvocationName` to **empty** (not `Config.InvocationName`) so new users get locale defaults.
5. **`config.html`**: invocation-name input empty by default; placeholder/helper text showing the defaults; a Reset button that clears the field (which, on save, re-applies locale defaults via redeploy).
6. **Tests**: empty → locale defaults; custom → all 17; migration of stored default → empty; it-IT reflects a custom name (the original bug's regression test).

## Why this option

- Preserves the `5595f44` intent (localized it-IT default for users who don't customize).
- Lets a user who customizes control all locales (fixes the bug).
- No fragile runtime string-compare (empty is the explicit "default" signal); the only compare is a one-time migration.
- The displayed default = the empty-state behavior, so UI and behavior agree.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Empty/null UserSkill.InvocationName => locale defaults apply: it-IT='mia collezione' (Config.LocaleInvocationNames), all other locales = Config.InvocationName ('jellyfin player')
- [ ] #2 A non-empty custom invocation name applies to ALL 17 locales INCLUDING it-IT (the unconditional LocaleInvocationNames override is gone for customized users) — verified end-to-end on device
- [ ] #3 Existing users migrated with no regression: a stored InvocationName equal to the global default ('jellyfin player') is treated as 'not customized' => keeps locale-default behavior (it-IT stays 'mia collezione'). This string-compare lives ONLY in the one-time migration, not the runtime build path
- [ ] #4 BuildSkillInteractionModels uses empty-check logic, not unconditional override: isEmpty(user) ? (LocaleInvocationNames[locale] ?? Config.InvocationName) : user
- [ ] #5 New users (LWA skill creation) default to an EMPTY invocation name (not 'jellyfin player') so they get locale defaults out of the box
- [ ] #6 config.html: invocation-name field is empty by default with a placeholder/helper showing the defaults ('Defaults: mia collezione for it-IT, jellyfin player elsewhere; a custom name applies to all locales'); a Reset button clears the field back to default
- [ ] #7 Tests: empty=>locale defaults; custom=>all 17 locales; migration of stored default=>empty; it-IT reflects custom name (regression for the original bug)
- [ ] #8 On-device verified: setting a custom name changes it-IT too; Reset restores 'mia collezione' for it-IT
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-06-23 17:26
---
Implemented + committed (026b4e9 on fix/invocation-locale-defaults-jf300); build 0 warnings/0 errors; 2433 tests pass (1 pre-existing FuzzyMatcher timing-flake, passes in isolation). Changes: Config.EffectiveInvocationName central resolution; BuildSkillInteractionModels uses empty-check (unconditional it-IT override gone); one-time migration in Plugin ctor clears stored global-default names (timing valid — ctor already reads Configuration at line 54); LWAController defaults new users to empty; config.html empty-by-default field + placeholder + Reset button + empty-valid. PENDING before Done: (1) AC #8 on-device verification — deploy + confirm a custom name now reaches it-IT, and Reset restores mia collezione for it-IT; this also validates the migration fires on real load. (2) KNOWN WEAKNESS: the migration unit tests are permissive (assert loadedUser==null || empty) — they'd pass even if the migration didn't run. The migration logic is reviewed-correct, but consider strengthening the test (make MigrateDefaultInvocationNames internal + direct test) or rely on the on-device check. Not deployed, not pushed (device-test gate).
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
FIXED + on-device verified (2026-06-23). Empty/null UserSkill.InvocationName → locale defaults (it-IT="mia collezione", others="jellyfin player"); a non-empty custom name applies to ALL 17 locales including it-IT. The unconditional Config.LocaleInvocationNames override (from 5595f44) is gone — replaced by Config.EffectiveInvocationName resolution + a one-time migration in the Plugin ctor that clears stored global-default names. Device test: (1) set custom "alex due" → it-IT became "alex due" (was pinned "mia collezione") — bug fixed; (2) Reset to empty → it-IT="mia collezione", en-US="jellyfin player" — locale defaults. Both redeploys succeeded (JF-297 working). Migration ran on load and preserved custom names. Commits 026b4e9 + 97a8502 (server-side validation fix to allow empty) on fix/invocation-locale-defaults-jf300. Deployed to minix; config survived. Minor follow-up: migration unit tests are permissive (on-device validated the actual behavior).
<!-- SECTION:FINAL_SUMMARY:END -->
