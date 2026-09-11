---
id: JF-513
title: >-
  Skill manifest declares only 12 of 17 locales: add ja-JP, pt-BR, ar-SA, nl-NL,
  hi-IN to manifest.json and redeploy (blocks simulate-skill for 5 locales)
status: Done
assignee: []
created_date: '2026-09-07 00:25'
updated_date: '2026-09-11 22:55'
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
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-12 night run, COMPLETE and live-verified. Implementation: 5 locale blocks (ar-SA/hi-IN/ja-JP/nl-NL/pt-BR) added to Alexa/Manifest/manifest.json + invariant test Manifest_DeclaresEveryInteractionModelLocale (set equality: manifest locales == embedded model locales == embedded Locale/*.json response-string locales; per-block shape incl. Source suffix). The test FAILS on the pre-change 12-locale manifest (proven: 5/17 missing) and passes after.

Deploy evidence (minix, Jellyfin 12.0.0 box): built both flavors with SDK 10 (0w/0e), hot-swapped the net10.0 DLL into AlexaSkill_0.12.1.0 with the full checklist (config backup BEFORE, 1 user preserved, chown abc:abc, active-DLL md5 match 390f174b...). Same-version startup pushes nothing (version-tag gate): the delivery path was POST custom-model/rebuild locale=* which always PUTs the embedded manifest first. First rebuild: manifest SUCCEEDED, 16/17 models SUCCEEDED, ja-JP FAILED.

ja-JP root cause (pre-existing, exposed by the first-ever build): the model had NEVER built on SMAPI - 49 samples carried the fullwidth question mark (U+FF1F, InvalidCharInSamples) and 4 bare-artist samples glued the slot to the particle ({musician}を再生, InvalidSample). This is also why get-interaction-model 404'd for ja-JP all along. Fixed in templates/ja-JP.yaml (samples end bare; the 4 forms get the space; header invariant rewritten; the 10 slot-type VALUE strings keep their question marks - type values are not samples). Single-locale rebuild then SUCCEEDED. Mirrors synced (VOICE_COMMANDS 39 strings, 6 ja mds edge labels, graphs.json x2 via parse_mermaid regen, docs-site/data.json embedded mermaid); response strings and prose keep theirs.

Final live state: get-skill-status = manifest SUCCEEDED + 17/17 interaction-model builds SUCCEEDED; simulate-skill reaches the plugin's real endpoint in all 5 new locales (nl: 'start jellyfin player' -> Dutch spoken response; pt-BR: 'abre jellyfin player'; ja-JP + ar-SA: 'open jellyfin player'; hi-IN: 'start jellyfin player'). NOTE: the literal English 'open' verb does not resolve under nl-NL/pt-BR/hi-IN NLU (marketplace launch-verb quirk, mirrors the e2e fixtures' localized open_utterance convention) - not a skill defect.

Gates: /simplify 4-angle (ctor factory, set-equality strengthening, validate_model.sh locale derivation + cwd anchor applied), code-review high (10 findings: applied or filed as JF-513.1 items, JF-513.2 rebuild-success-flag vacuous, JF-513.3). Build 0w/0e both flavors; full suite 3625/3625 green (twice); validators PASS. Deployed DLL md5-verified active; config survived. Side effects now live: catalog sync covers 17 locales (CatalogSyncLocales default *), first post-deploy sync watch item = JF-513.1 item 5 poll budget.

Commits: 713b0acd (fix+test), b20442cd (merge), c769ef32 (simplify pass), 2960ebfe (review pass + tracker), 09f0f162 (ja-JP buildable + mirrors), aab10906 (deploy skill 12.0 recipes). Pushed to main; CI green pending at close time.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Declared all 17 skill locales in the Alexa manifest (ar-SA/hi-IN/ja-JP/nl-NL/pt-BR blocks added with localized name/summary/description/examplePhrases, Source-suffix convention kept), plus an invariant test asserting manifest locales == embedded interaction-model locales == embedded response-string locales, so the JF-513 gap class (a model locale without a manifest block never builds and silently blocks simulate-skill) now fails the build instead of shipping.

The redeploy exposed why ja-JP had NEVER built: 49 samples carried the fullwidth question mark U+FF1F (InvalidCharInSamples) and 4 bare-artist samples glued the slot to the particle without a space (InvalidSample). Fixed in templates/ja-JP.yaml (samples end bare; {musician} を再生 gets the space; header invariant rewritten to the measured reality; the 10 slot-type VALUE strings keep their question marks). All mirrors synced (VOICE_COMMANDS, 6 ja mds, graphs.json x2 via parse_mermaid regen, docs-site/data.json).

Deployed to minix (net10.0 flavor, full config-backup checklist, active-DLL md5 verified) and pushed via the rebuild endpoint (same-version startup pushes nothing by design). Live verification: get-skill-status shows the manifest SUCCEEDED with 17/17 model builds SUCCEEDED, and simulate-skill exercises the plugin endpoint in all 5 new locales with locale-appropriate launch verbs. validate_model.sh now derives its all-mode locale list from the model files (was a stale hardcoded 12). Tests: full suite 3625/3625 green twice, validators PASS, build 0 warnings on both flavors. Review findings landed in JF-513.1 (stale locale references incl. CancelWords ja-JP re-vet, now unblocked), JF-513.2 (rebuild success flag vacuous on never-built locales), JF-513.3 (FindExistingSkill order dependence, catalog volume). Unblocks JF-399's ja/hi scope and the JF-511 e2e family for the 5 locales.
<!-- SECTION:FINAL_SUMMARY:END -->
