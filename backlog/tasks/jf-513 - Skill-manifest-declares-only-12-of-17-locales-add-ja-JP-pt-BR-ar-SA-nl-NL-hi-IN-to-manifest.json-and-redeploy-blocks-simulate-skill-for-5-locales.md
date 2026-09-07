---
id: JF-513
title: >-
  Skill manifest declares only 12 of 17 locales: add ja-JP, pt-BR, ar-SA, nl-NL,
  hi-IN to manifest.json and redeploy (blocks simulate-skill for 5 locales)
status: To Do
assignee: []
created_date: '2026-09-07 00:25'
labels:
  - i18n
  - smapi
  - manifest
dependencies: []
references:
  - JF-511
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-511 measurement phase (2026-09-07, live SMAPI evidence). The deployed skill's manifest declares only 12 locales (de-DE, en-AU, en-CA, en-GB, en-IN, en-US, es-ES, es-MX, es-US, fr-CA, fr-FR, it-IT). The repo's own static manifest at Jellyfin.Plugin.AlexaSkill/Alexa/Manifest/manifest.json contains exactly those 12; ja-JP, pt-BR, ar-SA, nl-NL, hi-IN are missing.

Measured impact: simulate-skill for the 5 missing locales fails with 'No interaction model was found for the specified locale' even though interaction models are SAVED for all 17 (profile-nlu works for every locale; get-interaction-model nl-NL returns content). get-skill-status's built-locale map also lists only the 12. So simulate-skill (device emulation) requires the locale to be present in the manifest, not merely to have a saved model.

Fix: add the 5 locales to publishingInformation.locales with localized name/summary/description/examplePhrases blocks (mirror the per-locale structure of the existing entries), redeploy via the plugin's skill update path, then verify with get-skill-status that 17 locales report SUCCEEDED builds and a simulate-skill open works in each of the 5 (e.g. nl-NL 'open jellyfin player' returns skillExecutionInfo).

Consumers: JF-511 (e2e for the other 15 locales) is blocked for these 5 locales until this lands; ja-JP also has no NLU fixture at all (16 locale NLU fixtures exist), which is a separate small gap to close alongside any ja-JP test work. Note: the examplePhrases in neighboring locale blocks are written in the locale's language; follow the same pattern. Also note the manifest's top-level publishingInformation summary fields are generic ('jellyfin', STREAMING_SERVICE); only locales need extending.
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
