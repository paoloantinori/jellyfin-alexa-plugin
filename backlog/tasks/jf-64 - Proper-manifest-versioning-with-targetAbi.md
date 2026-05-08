---
id: JF-64
title: Proper manifest versioning with targetAbi
status: Done
assignee: []
created_date: '2026-05-03 13:39'
updated_date: '2026-05-03 17:15'
labels:
  - enhancement
  - infrastructure
  - compatibility
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add proper targetAbi field to manifest.json for Jellyfin version compatibility. Inspired by the Streamyfin plugin pattern.

Currently the manifest.json may not properly declare which Jellyfin versions the plugin is compatible with. This can cause issues when Jellyfin updates its API surface.

Implementation:
1. Set targetAbi in manifest.json to match the current Jellyfin API version (e.g., "10.11.0.0")
2. Update the release script to bump targetAbi when building against new Jellyfin versions
3. Follow the Streamyfin pattern of explicit version declaration
4. Ensure the build process validates targetAbi against the referenced Jellyfin NuGet packages
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Updated release.sh to automatically derive targetAbi from the Jellyfin.Controller NuGet package version referenced in the csproj. The script now auto-generates manifest.json version entries with the correct targetAbi, sourceUrl, changelog, and timestamp. Existing manifest entries were already correct (0.1.x for 10.8, 0.2.x for 10.11).
<!-- SECTION:FINAL_SUMMARY:END -->
