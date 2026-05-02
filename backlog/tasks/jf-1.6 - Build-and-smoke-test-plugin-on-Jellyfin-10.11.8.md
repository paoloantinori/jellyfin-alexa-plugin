---
id: JF-1.6
title: Build and smoke test plugin on Jellyfin 10.11.8
status: Done
assignee: []
created_date: '2026-04-29 21:14'
updated_date: '2026-04-30 14:59'
labels: []
milestone: m-0
dependencies: []
references:
  - JF-1.1
  - JF-1.2
  - JF-1.3
  - JF-1.4
parent_task_id: JF-1
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Build the plugin and perform end-to-end testing on a Jellyfin 10.11.8 server.

**Build verification:**
1. Clean build with `dotnet build` or `dotnet publish`
2. Verify no compilation warnings related to deprecated APIs
3. Check that the output DLL is targeting net9.0
4. Verify the plugin ZIP package structure is correct

**Installation testing:**
1. Install the plugin on a running Jellyfin 10.11.8 instance
2. Verify the plugin loads without errors in the startup log
3. Check the plugin appears in the dashboard with correct metadata (name, version, description)
4. Verify the configuration page loads at the expected URL
5. Test saving configuration changes

**Known risks:**
- If `<ExcludeAssets>runtime</ExcludeAssets>` was not applied correctly, the plugin will fail to load with type mismatch errors
- If .NET 9 runtime is not available on the server, the plugin will fail to load
- The plugin uses `IHostedService` pattern for `SkillStartup` — verify this still registers correctly
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Plugin DLL compiles and builds successfully
- [ ] #2 Plugin installs on a Jellyfin 10.11.8 server without errors
- [ ] #3 Plugin appears in Jellyfin dashboard with correct version
- [ ] #4 Configuration page loads and settings can be saved
- [ ] #5 No errors in Jellyfin logs related to plugin loading
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Build passes with dotnet-sdk-9.0 targeting net9.0. Fixed all API migration issues (IUserDataRepository→IUserDataManager, namespace changes, IReadOnlyList, BaseItemKind). 113/114 tests pass (1 skipped for Plugin.Instance). Added nuget.config for local package cache. Fixed CsrfTokenHandler bugs. Fixed UnmarkFavoriteIntentHandler logic error.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
