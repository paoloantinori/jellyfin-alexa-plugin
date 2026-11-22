---
id: JF-934
title: >-
  nlu-lab: wait_for_build can pass on a stale SUCCEEDED row (locale never
  compared, no transition required)
status: Done
assignee: []
created_date: '2026-11-21 12:37'
updated_date: '2026-11-21 17:05'
labels:
  - nlu-lab
dependencies: []
references:
  - scripts/nlu-lab/smapi_ops.py
  - Jellyfin.Plugin.AlexaSkill/Alexa/SmapiManagement.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review finding (2026-11-21, Task 8 review of branch worktree-nlu-lab): scripts/nlu-lab/smapi_ops.py wait_for_build/parse_build_status never compares the status payload to the locale it just deployed. parse_build_status only checks lastModified.locale is non-empty; the locale PARAMETER is used only in the FAILED error message. Two failure shapes: (a) run B deploys a modified de-DE seconds after run A's de-DE build SUCCEEDED; the first post-deploy get-skill-status poll still carries the prior build's SUCCEEDED row, wait_for_build returns True immediately, and the battery profiles the OLD model while the run file pairs the rows with the NEW model's sha256. That is exactly the stale-model misdiagnosis class this lab exists to prevent (repo memory: the three-hour 'trainer basin' phantom reds, 2026-10-19). (b) Cross-locale: a stale de-DE SUCCEEDED row satisfies the wait after an it-IT deploy because locale is never compared. Fix at the right depth: require lastModified.locale == the deployed locale AND require an observed transition (record the pre-deploy lastModified and wait for it to change), not just a terminal status. Related, same function: parse_build_status's docstring claims to pin 'the shape the repo's own SmapiManagement.WaitForSkillStatusAsync consumes (interactionModel.lastModified.{status,locale})', but the C# method reads status.Manifest.LastModified.Status (SmapiManagement.cs ~388-392), the manifest resource, not interactionModel; correct the citation so the shape stands on its own evidence, not a false precedent. Tests must cover: stale-SUCCEEDED-same-locale, wrong-locale row, and the transition requirement.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed 2026-11-21 (commit on main): wait_for_build never trusts a stale build state. The fix landed in two rounds: the planned lastModified shape was proven FICTION by the code-reviewer's live read-only probe (controller re-probed independently), so the fix targets the REAL payload (interactionModel[<locale>].{eTag, lastUpdateRequest.status}, no lastModified anywhere): structural locale match, eTag as the per-build transition token, stale predicate on eTag equality alone (mid-build status flips on the same eTag never satisfy), get_build_entry distinguishing absent-entry (silent, fresh skill) from read-failure (loud DEGRADED warning), parse_build_status/get_build_row deleted. The C# consumer of the same drift is filed separately as JF-938. Residual (b) recorded for the Task 12 bench: a byte-identical redeploy may mint no new eTag (A/A then does deploy-once-probe-twice). Gates: /simplify battery, code-review high (the live-probe round), re-review APPROVED; suite 116 passed.
<!-- SECTION:FINAL_SUMMARY:END -->
