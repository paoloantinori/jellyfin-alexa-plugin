---
id: JF-53
title: Enhanced configuration page with diagnostics
status: Done
assignee: []
created_date: '2026-05-03 13:38'
updated_date: '2026-05-03 18:45'
labels:
  - enhancement
  - ux
  - configuration
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Enhance the plugin web configuration page with connection testing, OAuth status display, and usage statistics. Inspired by the SSO plugin, Streamyfin plugin, and Meilisearch plugin patterns.

Current config page has basic fields (server URL, SSL type, LWA credentials). Enhance with:
1. "Test Connection" button (ties into B5 task)
2. OAuth token status indicator (valid/expired/never-linked)
3. Per-user skill status (which users have linked, last activity)
4. Intent usage statistics (most-used intents, daily/weekly counts)
5. Health indicators (server connection status, last error, response time)
6. Diagnostics section linking to B7 metrics

Use existing Jellyfin emby-* web components for consistent styling.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added health summary bar showing plugin status, version, user count, and request metrics loaded from diagnostics API. Added Token column to user skill table showing SMAPI token state (valid/expired/recoverable/none) with color-coded indicators.
<!-- SECTION:FINAL_SUMMARY:END -->
