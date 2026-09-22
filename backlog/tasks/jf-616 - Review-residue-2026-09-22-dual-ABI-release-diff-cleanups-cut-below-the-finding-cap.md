---
id: JF-616
title: >-
  Review residue 2026-09-22: dual-ABI release diff cleanups cut below the
  finding cap
status: To Do
assignee: []
created_date: '2026-09-22 12:56'
labels:
  - code-review-residue
  - release-pipeline
  - documentation
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Residue from the 2026-09-22 high-effort review of the uncommitted dual-ABI release diff (csproj 12.1.0 bump, two-zip release workflow, two-entry manifest script). These were verified but fell below the review's 10-finding output cap; they are documentation/config hygiene, not correctness. The main findings (manifest entry ordering vs Jellyfin catalog selection, NETSDK1129 solution publish, same-version supersession, placeholder keying, targetAbi 12.0.0.0 claim) were reported in the review itself and are NOT part of this task.

Items:
1. ABI_LINES in .github/workflows/add_release_to_manifest.py carries a publish_dir key nothing reads (the workflow hardcodes the cd paths); drop it or make the script derive paths from it so the table stops advertising a knob that does not exist.
2. Jellyfin.Plugin.AlexaSkill/Jellyfin.Plugin.AlexaSkill.csproj line 9 comment still justifies the Condition split with "(12.0.0-rc7 ships net10.0-only libs)" while the refs now say 12.1.0; restate the constraint for 12.1.0 or drop the version-specific parenthetical.
3. Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/RepeatIntentHandler.cs line 145 pins an invariant verification to "v10.11.8 and v12.0-rc7"; the build now compiles against 12.1.0, so the comment claims verification against a version no longer referenced.
4. CLAUDE.md still describes net10.0/Jellyfin 12 as "the pre-release line" with "12.0.0-rc7 for net10.0", and its Release section describes a one-zip, one-placeholder-entry flow (singular "the zip", one targetAbi); both contradict the dual-shipping-line design this diff introduces. Update when the diff lands.
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
