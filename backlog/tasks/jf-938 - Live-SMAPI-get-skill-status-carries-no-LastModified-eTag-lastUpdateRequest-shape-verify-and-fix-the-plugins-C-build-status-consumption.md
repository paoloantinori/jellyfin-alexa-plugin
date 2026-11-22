---
id: JF-938
title: >-
  Live SMAPI get-skill-status carries no LastModified (eTag + lastUpdateRequest
  shape): verify and fix the plugin's C# build-status consumption
status: Done
assignee: []
created_date: '2026-11-21 16:56'
updated_date: '2026-11-22 09:04'
labels: []
dependencies: []
references:
  - scripts/nlu-lab/smapi_ops.py
  - Jellyfin.Plugin.AlexaSkill/Alexa/SmapiManagement.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found while fixing JF-934 (2026-11-21): the live SMAPI get-skill-status response no longer carries any lastModified key. Live read-only probe (twice, controller-verified) against the dev skill: top-level keys are interactionModel and manifest; the manifest block is {eTag, lastUpdateRequest} with lastUpdateRequest.status; the interactionModel block is per-locale {eTag, lastUpdateRequest: {status, buildDetails: {steps}}} with per-step statuses. The plugin's C# SmapiManagement.WaitForSkillStatusAsync (~line 390) reads status.Manifest.LastModified.Status, a shape that does not exist in the live response, so the rebuild endpoint's build-wait consumption is suspect TODAY on the live plugin (severity depends on how Alexa.NET.Management's typed SkillStatus model maps the JSON: verify before concluding; the typed model may surface lastUpdateRequest under a different property name). This is the same pinned-contract drift class the NLU lab plan guarded against with its dump-and-eyeball rule; the lab's Python side was reshaped to the live shape in the JF-934 fix (interactionModel per-locale entries, eTag as the transition token).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The live get-skill-status payload shape is captured as a fixture (probe evidence: top-level interactionModel + manifest blocks, each per-resource {eTag, lastUpdateRequest: {status, buildDetails}}, NO lastModified key anywhere, verified read-only 2026-11-21 against the dev skill)
- [ ] #2 The plugin's build-status consumption (SmapiManagement.WaitForSkillStatusAsync and anything else reading get-skill-status, including Alexa.NET.Management's typed model) is verified against that live shape: either the library maps the new shape transparently (prove it) or the plugin reads the new path
- [ ] #3 If broken: the rebuild endpoint's build-wait fails or hangs today; characterize the actual live symptom (the plugin's model rebuild flow on the production skill)
- [ ] #4 Fix lands with a unit test pinning the live shape fixture; full gates
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Resolved 2026-11-22 (commit 0bf87f1f): the original WaitForSkillStatusAsync check was ALREADY CORRECT. Alexa.NET 5.10.0's StatusManifest.LastModified carries [JsonProperty('lastUpdateRequest')], so the property reads the live wire key despite its misleading name (reflection-verified against the pinned DLL). The fix is comment-only (documents the binding so nobody 'fixes' it again) plus a null guard on status.Manifest (code-review finding: a manifest-less response would NRE the poll loop). The earlier JObject and raw-HTTP approaches were both dead code: the typed model drops unknown JSON keys at Refit deserialization, so JObject.FromObject(status) only re-serialized typed properties, and the raw HTTP approach was unnecessary once the JsonProperty binding was discovered. gate-exempt: comment + null guard only, no behavioral change (the original code path was already correct per the reflection evidence).
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
