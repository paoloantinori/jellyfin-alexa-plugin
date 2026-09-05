---
id: JF-495
title: >-
  Silent live-model regression to yesterday's model (1385 samples) between
  13:53-14:56 2026-09-05: find the deploy path that PUT a stale model and harden
  every model-deploy source
status: To Do
assignee: []
created_date: '2026-09-05 13:04'
labels:
  - incident
  - smapi
  - model-deployment
dependencies: []
references:
  - corr=2c86b74b
  - JF-493
  - CatalogManager.UpdateInteractionModelAsync
  - rebuild endpoint locale fallback
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
FORENSIC INCIDENT 2026-09-05: between 13:53 and 14:56 the live it-IT interaction model was silently REPLACED on SMAPI with yesterday's version (59 intents / 1385 samples instead of 60/1402: PlayNextEpisodeIntent and the 10 JF-490 verb-ful che-si-chiama anchors gone). Effect on device: EVERY utterance fell to AMAZON.FallbackIntent (even 'chiedi a mia collezione di suonare i pixies'; requests arrived at the skill as Fallback, corr=2c86b74b/abf83007/410614e6 14:56-14:57). Contained at ~15:05 by redeploying from the working tree (60/1427) via ask smapi set-interaction-model; profile-nlu confirmed routing restored.

EXONERATED: the JF-493 worker (no network calls, timing 12:00-12:45 vs the 13:53-14:56 window, in-process fake SMAPI only); the CatalogSyncTask 12:54 run (its UpdateInteractionModelAsync is GET-live-model -> inject catalog refs -> PUT the SAME model; it cannot replace the intent set, and it ran before the window while 13:51 device tests still worked).

CANDIDATES TO INVESTIGATE (in the plugin's model-deploy code paths):
1. The 16-locale background rebuild loop (12:58-13:15, orchestrator-run): for the 5 INACTIVE locales (hi-IN, ja-JP, nl-NL, pt-BR, ar-SA) the endpoint answered 'Rebuilt 1 locale(s) - 0 succeeded, 0 failed'. Verify what the custom-model/rebuild endpoint does for a locale the skill does not carry: is there a CustomModelLocale DEFAULT fallback (the documented fallback is it-IT!) that rebuilds/RESTORES it-IT from a STORED SNAPSHOT? Where are stored/fetched custom models persisted (CustomModelDeployment 'fetch, deploy, restore' feature) and can a restore path serve a stale (yesterday's 1385) it-IT snapshot?
2. A stored model snapshot path: the 1385 model existed ONLY in yesterday evening's DLL; if the plugin persists fetched models (restore feature) and any path PUTs them, find it.
3. Timing anomaly to explain: device tests at 13:51 worked with 1402; async builds submitted by the loop ended by ~13:15; the regression manifested by 14:56. Check whether an async/queued model PUT from the loop's inactive-locale calls could land late.

REPRO/HARDENING GOALS: identify the exact code path; make every model deploy source-fresh (DLL-embedded or explicit payload, never a stale snapshot); log every set-interaction-model submission with source + intent/sample counts (the 12:54 sync logs 'Fetching/Pushing interaction model' but a silent path would leave no trace: this incident had NONE); consider a post-deploy canary assertion comparing deployed sample counts to expected.

References: corr=2c86b74b, abf83007, 410614e6 (the Fallback storm); containment redeploy 15:05; CatalogManager.UpdateInteractionModelAsync; CustomModelDeployment; Controller rebuild endpoint.
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
