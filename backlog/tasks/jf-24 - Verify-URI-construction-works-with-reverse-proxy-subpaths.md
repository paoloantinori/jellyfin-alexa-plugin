---
id: JF-24
title: Verify URI construction works with reverse proxy subpaths
status: Done
assignee:
  - claude
created_date: '2026-05-01 06:21'
updated_date: '2026-05-01 08:22'
labels:
  - bug
  - robustness
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
BUG from upstream: URI construction may break when Jellyfin is behind a reverse proxy with a subpath (e.g., https://example.com/jellyfin/).

new Uri(baseUri, "/Items/...") ignores the base path because of the leading slash. We already use "Items/" without leading slash in GetStreamUrl, but need to verify ALL URI construction paths:

Check:
- BaseHandler.GetStreamUrl() - uses "Items/" (correct)
- PlaybackNearlyFinishedEventHandler - uses "Audio/" paths (verify)
- All stream URL generation - verify relative URI construction
- Cover art URL generation (future) - plan for subpath support

Reference: lePerdu fork commit 07cd5e24
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Verified and fixed URI construction for reverse proxy subpaths.

**Findings:** All URI construction paths already use relative paths without leading slashes (correct pattern). The real bug was that `ServerAddress` could lack a trailing slash, causing `new Uri(base, relative)` to discard the last path segment (e.g., `/jellyfin` → `/jellyfin` is treated as a file, not a directory).

**Fix:** Added `NormalizeTrailingSlash()` to the ServerAddress setter — trims extra slashes and ensures exactly one trailing slash. This guarantees correct relative URI resolution for all consumers (GetStreamUrl, Audio URLs, manifest endpoints, account-linking URLs).

**Tests:** 9 new tests covering trailing slash normalization and URI resolution with subpaths (Items/, Audio/, account-linking). All 163 tests pass.

**Simplified:** TestHelpers.SetServerAddress now uses the property setter directly instead of reflection.
<!-- SECTION:FINAL_SUMMARY:END -->
