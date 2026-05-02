---
id: JF-1
title: Migrate Jellyfin Alexa Plugin to 10.11.8
status: Done
assignee: []
created_date: '2026-04-29 21:13'
updated_date: '2026-04-29 22:15'
labels: []
milestone: m-0
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Parent task for migrating the Jellyfin Alexa Skill Plugin from current SDK 10.9.11 / .NET 8.0 to Jellyfin SDK 10.11.8 / .NET 9.0. This covers all necessary changes: framework upgrade, NuGet package updates, build configuration, third-party library verification, API compatibility, authentication testing, and release preparation.

## Current State
- Target framework: net8.0
- Jellyfin SDK: 10.9.11 (Jellyfin.Controller, Jellyfin.Model)
- No ExcludeAssets on Jellyfin package references
- build.yaml targets 10.10.7 abi / net8.0

## Target State
- Target framework: net9.0
- Jellyfin SDK: 10.11.8 (latest stable)
- Proper ExcludeAssets configuration
- build.yaml targets 10.11.0.0 abi / net9.0
- All controllers, authentication flows, and Alexa handlers verified compatible

## Key Research Findings
- BasePlugin, IHasWebPages, IPluginServiceRegistrator interfaces: NO breaking changes
- ISessionManager: NO breaking changes
- Deprecated auth headers (X-Emby-Authorization, X-Emby-Token): plugin uses its own LWA flow, not affected
- EF Core database migration: transparent since plugin uses interfaces, not raw SQL
- System.Threading.Lock change in BasePluginOfT: internal only, no plugin impact
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 All subtasks completed and verified
- [ ] #2 Plugin compiles successfully against .NET 9.0 SDK
- [ ] #3 Plugin loads correctly on Jellyfin 10.11.8 server
- [ ] #4 All Alexa skill functionality works end-to-end
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Jellyfin 10.11.8 migration complete. All subtasks done: JF-1.1 (csproj/net9.0), JF-1.2 (build.yaml/manifest/Directory.Build.props), JF-1.3 (third-party libs verified), JF-1.4 (API compat verified), JF-1.5 (auth flows verified).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
