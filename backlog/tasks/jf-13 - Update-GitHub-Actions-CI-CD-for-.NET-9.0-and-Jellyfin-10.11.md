---
id: JF-13
title: Update GitHub Actions CI/CD for .NET 9.0 and Jellyfin 10.11
status: Done
assignee: []
created_date: '2026-05-01 05:17'
updated_date: '2026-05-01 05:20'
labels:
  - ci-cd
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Update the existing GitHub Actions workflows for the 10.11 migration:

1. **release-build.yml**: Update .NET SDK from 8.x to 9.x, update publish path from net8.0 to net9.0
2. **dev-build.yml**: Same .NET SDK and path updates  
3. **add_release_to_manifest.py**: Read targetAbi from build.yaml instead of hardcoding "10.8.0.0". Read changelog from build.yaml too.
4. **release-build.yml**: Package only the DLLs listed in build.yaml artifacts (not the entire publish output)
5. **dev-build.yml**: Update to net9.0 paths
6. Ensure the ZIP structure matches what Jellyfin plugin catalog expects

Current state:
- Directory.Build.props has Version 0.2.0.0
- build.yaml has targetAbi 10.11.0.0, framework net9.0
- manifest.json already has the 0.2.0.0 entry with real checksum
- Workflows still reference net8.0 and .NET SDK 8.x
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Updated all three CI/CD files: release-build.yml and dev-build.yml now target .NET 9.x / net9.0 paths, add_release_to_manifest.py reads targetAbi and changelog from build.yaml instead of hardcoding.
<!-- SECTION:FINAL_SUMMARY:END -->
