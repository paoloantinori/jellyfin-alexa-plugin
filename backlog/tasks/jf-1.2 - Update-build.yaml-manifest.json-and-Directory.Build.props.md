---
id: JF-1.2
title: 'Update build.yaml, manifest.json, and Directory.Build.props'
status: Done
assignee: []
created_date: '2026-04-29 21:14'
updated_date: '2026-04-29 21:51'
labels: []
milestone: m-0
dependencies: []
references:
  - JF-1.1
parent_task_id: JF-1
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Update all build and manifest configuration files for the new release targeting Jellyfin 10.11.8.

**Files to modify:**

### 1. `build.yaml`
```yaml
# Change:
targetAbi: "10.11.0.0"    # was "10.10.7"
framework: "net9.0"        # was "net8.0"
```

### 2. `manifest.json`
Add a new version entry to the versions array:
```json
{
    "version": "x.y.z.0",
    "changelog": "Updated for Jellyfin 10.11.x compatibility. Requires .NET 9.0 runtime.",
    "targetAbi": "10.11.0.0",
    "sourceUrl": "https://github.com/.../releases/download/vx.y.z/jellyfin-plugin-alexaskill_x.y.z.0.zip",
    "checksum": "...",
    "timestamp": "2026-04-29T00:00:00Z"
}
```
The exact version number should match Directory.Build.props.

### 3. `Directory.Build.props`
Update the `<Version>` property for the new release. Follow the existing versioning pattern.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 build.yaml targetAbi updated to 10.11.0.0
- [ ] #2 build.yaml framework updated to net9.0
- [ ] #3 manifest.json has new version entry with correct targetAbi
- [ ] #4 Directory.Build.props Version updated for new release
- [ ] #5 All config files are consistent with the same version
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Updated build.yaml (targetAbi 10.11.0.0, framework net9.0), manifest.json (added 0.2.0.0 release entry), and Directory.Build.props (bumped to 0.2.0.0).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
