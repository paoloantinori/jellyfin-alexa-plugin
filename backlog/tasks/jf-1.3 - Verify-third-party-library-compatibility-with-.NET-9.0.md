---
id: JF-1.3
title: Verify third-party library compatibility with .NET 9.0
status: Done
assignee: []
created_date: '2026-04-29 21:14'
updated_date: '2026-04-29 22:15'
labels: []
milestone: m-0
dependencies: []
references:
  - JF-1.1
parent_task_id: JF-1
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Verify that all third-party NuGet dependencies used by the plugin are compatible with .NET 9.0. Update any that are not.

**Current third-party dependencies:**
- `Alexa.NET` (1.22.0) — Core Alexa skill SDK
- `Alexa.NET.Management` (5.10.0) — Alexa skill management API
- `Amazon.Lambda.Core` (2.1.0) — AWS Lambda core
- `Amazon.Lambda.Serialization.Json` (2.1.0) — JSON serialization
- `Refit` — HTTP client library (version not pinned in csproj)

**Verification approach:**
1. Check each package on NuGet.org for net9.0/net8.0 target compatibility
2. Most .NET Standard 2.0 / net8.0 packages work on net9.0 without changes
3. If a package is incompatible, find the latest compatible version
4. Pay special attention to `Amazon.Lambda` packages which may lag behind .NET releases
5. `Refit` should be fine as it's actively maintained

**Risk:** The Amazon.Lambda packages were at 2.1.0 and may not explicitly target net9.0. However, net8.0-targeted packages are generally forward-compatible with net9.0. If issues arise, check for newer versions or consider if these packages are actually needed (the plugin doesn't run in Lambda — it runs inside Jellyfin).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Alexa.NET 1.22.0 verified compatible with net9.0 (or updated)
- [ ] #2 Alexa.NET.Management 5.10.0 verified compatible with net9.0 (or updated)
- [ ] #3 Amazon.Lambda packages verified compatible with net9.0 (or updated)
- [ ] #4 Refit verified compatible with net9.0
- [ ] #5 Plugin compiles and all third-party dependencies resolve correctly
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
All third-party NuGet packages verified compatible with .NET 9.0. Alexa.NET 1.22.0, Alexa.NET.Management 5.10.0, Amazon.Lambda.Core 2.1.0, Amazon.Lambda.Serialization.Json 2.1.0 all target .NET Standard 2.0 (universal compatibility). Microsoft.Extensions.Logging.Console 9.0.* explicitly supports net9.0. No updates required.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
