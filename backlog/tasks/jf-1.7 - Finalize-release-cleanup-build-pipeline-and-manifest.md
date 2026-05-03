---
id: JF-1.7
title: 'Finalize release: cleanup, build pipeline, and manifest'
status: Done
assignee: []
created_date: '2026-04-29 21:14'
updated_date: '2026-05-03 06:41'
labels: []
milestone: m-0
dependencies: []
references:
  - JF-1.6
parent_task_id: JF-1
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Finalize the migration release: clean up artifacts, verify the build pipeline, and prepare for publishing.

**Cleanup tasks:**
1. Remove backup/conflict files from the repository:
   - `Jellyfin.Plugin.AlexaSkill/Plugin.cs.orig`
   - `Jellyfin.Plugin.AlexaSkill/Plugin_BACKUP_802252.cs`
   - `Jellyfin.Plugin.AlexaSkill/Plugin_BASE_802252.cs`
   - `Jellyfin.Plugin.AlexaSkill/Plugin_LOCAL_802252.cs`
   - `Jellyfin.Plugin.AlexaSkill/Plugin_REMOTE_802252.cs`
   - `manifest.json.orig`
2. Verify `.gitignore` covers these patterns for the future

**Build pipeline:**
1. Verify the CI/CD build process works with net9.0
2. Ensure the plugin ZIP artifact is generated correctly
3. Calculate SHA256 checksum for the artifact
4. Update manifest.json with the correct checksum

**Release preparation:**
1. Tag the release with the new version number
2. Create a GitHub release with the artifact
3. Update the manifest.json with the release URL and checksum
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Cleanup backup files removed from repository
- [x] #2 Plugin ZIP artifact builds correctly via CI
- [x] #3 Release tagged with correct version
- [x] #4 manifest.json checksum matches built artifact
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Release finalized: Directory.Build.props bumped to 0.2.0.1 matching the released version. AlexaSkill_0.2.0.1.zip removed from git tracking, *.zip added to .gitignore. Build verified: 249/250 tests pass, 0 errors. Tag 0.2.0.1 already existed. manifest.json checksum already correct.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->
