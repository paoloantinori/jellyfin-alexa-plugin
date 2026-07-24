---
id: JF-156
title: Investigate and fix failing GitHub Actions CI pipeline
status: Done
assignee: []
created_date: '2026-05-16 07:29'
updated_date: '2026-05-16 08:42'
labels:
  - ci/cd
  - github-actions
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
GitHub Actions jobs are failing. See run: https://github.com/paoloantinori/jellyfin-alexa-plugin/actions/runs/25955824406

The CI pipeline was copied from the original upstream jellyfin-alexa-plugin project and has never been properly revisited or adapted. The failures may be due to:
- Workflow definitions that reference upstream repo structure or secrets
- Build/test steps that don't match our current project layout
- Missing or incorrect environment setup steps
- Dependency or SDK version mismatches

Steps:
1. Review the failing workflow YAML files in `.github/workflows/`
2. Check the specific error logs from the failed run
3. Compare with current project structure and build requirements
4. Update workflows to match our actual build/test/release process
5. Verify fixes by pushing and monitoring a new run
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed CI pipeline: CodeQL upgraded to v3 actions and removed JS from language matrix. Release workflow now uses peter-evans/create-pull-request@v6 instead of direct push to main.
<!-- SECTION:FINAL_SUMMARY:END -->
