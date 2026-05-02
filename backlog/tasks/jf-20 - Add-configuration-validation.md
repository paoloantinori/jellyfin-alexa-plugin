---
id: JF-20
title: Add configuration validation
status: Done
assignee:
  - claude
created_date: '2026-05-01 06:02'
updated_date: '2026-05-01 08:15'
labels:
  - robustness
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
MEDIUM: PluginConfiguration setters have no validation. Invalid config causes runtime failures.

Changes:
- Validate ServerAddress is a valid absolute URI
- Validate LwaClientId/LwaClientSecret are non-empty when users exist
- Validate no duplicate user IDs in Users list
- Add null guards in SslCertType setter (currently accesses Plugin.Instance which can be null)
- Return clear validation error messages to the Jellyfin admin UI
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented configuration validation with null-safe property setters.

**Changes:**
- Extracted duplicated ManifestSkill update logic into private `UpdateManifestSkill()` with null guard for `Plugin.Instance`
- Added `Validate()` method checking: ServerAddress URI format (HTTP/HTTPS only), LWA credentials required when users exist, duplicate user ID detection
- 13 new tests: URI validation (valid/invalid/FTP/empty), LWA credential requirements, duplicate detection, null-safe setter verification

All 154 tests pass (24 config tests + 130 others).
<!-- SECTION:FINAL_SUMMARY:END -->
