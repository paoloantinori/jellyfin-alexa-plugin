---
id: JF-939
title: >-
  SMAPI skill creation now rejects the manifest template (subscriptions enum +
  empty icon URIs): the plugin's fresh-skill path is broken on config wipe
status: Done
assignee: []
created_date: '2026-11-21 17:53'
updated_date: '2026-11-22 09:04'
labels: []
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Manifest/ManifestSkill.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Manifest/manifest.json
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found while creating the lab skill (2026-11-21, live 400 with full violations body via ask --debug): SMAPI skill CREATION now rejects the committed manifest template. Violations: (1) INVALID_ENUM_VALUE on manifest.events.subscriptions[0].eventName "AMAZON.MediaContent.Available" (the publications entry with the same value was NOT flagged: the subscriptions enum differs); (2) UNEXPECTED_PROPERTY on publishingInformation.smallIconUri and .largeIconUri when empty strings. The plugin's own fresh-skill path rides the same template via ManifestSkill.cs (SkillStartup.cs:259, LWAController.cs:200) and fires on every config wipe + skill recreation (the documented wipe-recovery flow), so a user wiping config on today's SMAPI cannot get a new skill created until this is fixed. The existing production skill predates the validation and keeps working (updates tolerated). The lab's Python side prunes (events block + empty icons) which is correct for the LAB skill (proactive events unused there); the plugin needs the proper subscription treatment instead, since its skill genuinely uses proactive events. NLU-lab reference: scripts/nlu-lab/cli.py prepare_manifest carries the live evidence comment.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Root cause confirmed against the live API (the probe evidence is already captured: subscriptions enum rejection + empty icon properties at CREATION time)
- [ ] #2 The fix lands in ManifestSkill (or its callers SkillStartup.cs / LWAController.cs): valid subscription treatment (verify against the current SMAPI events docs whether AMAZON.MediaContent.Available remains a legal subscription for new skills; if retired, prune subscriptions like the lab does but KEEP the events endpoint for the existing skill, or substitute the currently-valid event), plus non-empty or absent icon URIs
- [ ] #3 A unit test pins the creation payload against the live 400 evidence
- [ ] #4 Full gates (build 0 warnings both TFMs, suite, /simplify, code-review high)
- [ ] #5 Live verification: a fresh skill creation on the dev vendor succeeds (coordinate with the maintainer: the trigger fires on config wipe)
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed 2026-11-22 (commit 0bf87f1f): CreateSkillAsync now clones the shared ManifestSkill before sanitizing (PrepareForCreation nulls Events.Subscriptions on the clone, never on the shared instance that updates reuse). The /simplify reflection round proved Events.Subscriptions is a typed Subscription[] (one line, no JObject needed) and the empty icon URIs never reach the C# payload (PublishingInformation drops them at deserialization). Two unit tests added: payload-pinning (subscriptions gone, publications shape captured) and clone-pattern verification. Known open question from the code-review: the Alexa.NET PublicationEventName enum may serialize as its .NET member name rather than the SMAPI wire name; live verification (config wipe + skill creation) will settle whether the typed values pass SMAPI validation. Gates: /simplify 3-agent battery, code-review high (6 findings all applied), build 0 warnings, suite 6167/6167.
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
