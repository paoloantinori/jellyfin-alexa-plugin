---
id: JF-1.7
title: 'Finalize release: cleanup, build pipeline, and manifest'
status: In Progress
assignee: []
created_date: '2026-04-29 21:14'
updated_date: '2026-04-30 15:06'
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
- [ ] #1 Cleanup backup files removed from repository
- [ ] #2 Plugin ZIP artifact builds correctly via CI
- [ ] #3 Release tagged with correct version
- [ ] #4 manifest.json checksum matches built artifact
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Cleanup completed: backup files removed, .gitignore covers patterns. Manifest changelog updated. Build verified passing with 113+ tests. Remaining: build release ZIP with `dotnet publish`, compute MD5 checksum, update manifest.json checksum, tag release, create GitHub release.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
