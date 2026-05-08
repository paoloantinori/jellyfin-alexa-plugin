---
id: JF-62
title: Fix plugin icon not displaying in Jellyfin
status: Done
assignee: []
created_date: '2026-05-03 13:39'
updated_date: '2026-05-03 17:06'
labels:
  - bug
  - ux
  - configuration
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Fix the plugin icon/image not displaying in the Jellyfin plugin list page. Reported as still broken in v0.2.0.4.

The plugin's image should appear next to the plugin name in Jellyfin's dashboard plugin list, but it currently shows as blank/generic.

Investigation needed:
1. Check manifest.json for correct image URL and path
2. Verify the image is embedded as a web resource via IHasWebPages
3. Ensure the image file is included in the build output and DLL resources
4. Check that the image URL matches what Jellyfin expects in the plugin manifest
5. Compare with other plugins that successfully display their icons (e.g., SSO plugin, Intro Skipper)
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added imageUrl field to manifest.json pointing to icon.png hosted on GitHub raw content. Jellyfin uses this manifest field (not embedded resources or IHasWebPages) to display plugin icons in the dashboard. The icon file already existed at the repo root; only the manifest reference was missing.
<!-- SECTION:FINAL_SUMMARY:END -->
