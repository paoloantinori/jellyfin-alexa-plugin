---
id: JF-1.1
title: Update csproj to target .NET 9.0 and Jellyfin SDK 10.11.8
status: Done
assignee: []
created_date: '2026-04-29 21:14'
updated_date: '2026-04-29 21:49'
labels: []
milestone: m-0
dependencies: []
parent_task_id: JF-1
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Update the project file `Jellyfin.Plugin.AlexaSkill/Jellyfin.Plugin.AlexaSkill.csproj` for .NET 9.0 and Jellyfin SDK 10.11.8.

**Specific changes required:**

1. Change `<TargetFramework>net8.0</TargetFramework>` to `<TargetFramework>net9.0</TargetFramework>`

2. Update Jellyfin package references:
```xml
<PackageReference Include="Jellyfin.Controller" Version="10.11.8">
    <ExcludeAssets>runtime</ExcludeAssets>
</PackageReference>
<PackageReference Include="Jellyfin.Model" Version="10.11.8">
    <ExcludeAssets>runtime</ExcludeAssets>
</PackageReference>
```
The `<ExcludeAssets>runtime</ExcludeAssets>` is **critical** — without it, the plugin DLLs will conflict with the server's own copies at runtime and the plugin won't register correctly.

3. Update `Microsoft.Extensions.Logging.Console` from `6.0.*` to `9.0.*` to match the .NET 9 runtime.

**Why ExcludeAssets matters:** Jellyfin already ships Jellyfin.Controller and Jellyfin.Model assemblies. If the plugin bundles its own copies, type identity mismatches cause the plugin to fail loading. ExcludeAssets prevents the plugin from bundling these DLLs.

**Prerequisite:** .NET SDK 9.0 must be installed on the build machine.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 csproj TargetFramework changed from net8.0 to net9.0
- [ ] #2 Jellyfin.Controller updated to 10.11.8 with ExcludeAssets runtime
- [ ] #3 Jellyfin.Model updated to 10.11.8 with ExcludeAssets runtime
- [ ] #4 Microsoft.Extensions.Logging.Console updated to 9.0.* (or removed if unnecessary)
- [ ] #5 Plugin compiles without errors on .NET 9.0 SDK
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Updated csproj: net8.0 -> net9.0, Jellyfin.Controller/Model 10.9.11 -> 10.11.8 with ExcludeAssets runtime, Microsoft.Extensions.Logging.Console 6.0.* -> 9.0.*. Updated test project to net9.0. Fixed trailing space in Amazon.Lambda.Core version. Cannot verify compilation without .NET 9.0 SDK installed.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
