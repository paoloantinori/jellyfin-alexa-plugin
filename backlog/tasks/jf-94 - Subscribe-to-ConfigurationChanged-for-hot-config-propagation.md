---
id: JF-94
title: Subscribe to ConfigurationChanged for hot config propagation
status: Done
assignee: []
created_date: '2026-05-07 06:08'
updated_date: '2026-05-07 07:22'
labels: []
dependencies: []
references:
  - claudedocs/research_jellyfin_plugin_best_practices_2026-05-07.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Context
The OpenSubtitles plugin demonstrates a pattern where active services react to configuration changes without requiring a Jellyfin restart. Our plugin currently requires a restart when changing server address, SSL settings, or LWA credentials — because services like `JellyfinConnectivityChecker`, `CircuitBreaker`, `SearchResultCache`, and `SkillStartup` cache or initialize based on config at startup time.

## What
Subscribe to the `ConfigurationChanged` event (provided by `BasePlugin<TConfiguration>`) and propagate relevant changes to active services:
- Invalidate `JellyfinConnectivityChecker` cache when `ServerAddress` changes
- Reset `CircuitBreaker` circuits when `ServerAddress` changes
- Optionally clear `SearchResultCache` on significant config changes
- Re-evaluate `ManifestSkill` endpoint (already partially handled by `PluginConfiguration.UpdateManifestSkill()`)

## Why
Operational — avoids needing restarts when admins update server address or SSL settings. Users see config take effect immediately.

## Key Files
- `Plugin.cs` — subscribe to `ConfigurationChanged` event
- `Diagnostics/JellyfinConnectivityChecker.cs` — add `InvalidateCache()` method
- `Alexa/CircuitBreaker.cs` — already has `Reset()`, just needs to be called
- `Alexa/Cache/SearchResultCache.cs` — add `Clear()` method
- `EntryPoints/SkillStartup.cs` — wire up event subscription

## Reference
- OpenSubtitles plugin pattern: `ConfigurationChanged += (_, _) => { Downloader.Instance?.ConfigurationChanged(Configuration); };`
- Research report: `claudedocs/research_jellyfin_plugin_best_practices_2026-05-07.md` (Recommendation #1)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Plugin subscribes to ConfigurationChanged event from BasePlugin<T>
- [ ] #2 JellyfinConnectivityChecker gets InvalidateCache() method that resets cached result and timestamp
- [ ] #3 CircuitBreaker.Reset() is called when ServerAddress changes
- [ ] #4 SearchResultCache gets Clear() method that removes all entries
- [ ] #5 Config changes to ServerAddress invalidate connectivity cache and reset circuit breaker
- [ ] #6 Unit tests verify each service reacts correctly to config change notification
- [ ] #7 No restart required for ServerAddress, SslCertType, or LWA credential changes to take effect
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Starting implementation. Exploring key files to understand current patterns.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Subscribed to ConfigurationChanged event in Plugin constructor with change-detection guard (only resets when ServerAddress changes)

Added InvalidateCache() to JellyfinConnectivityChecker with semaphore thread safety

Added Clear() and Count to SearchResultCache with NoopSearchResultCache override

Wired ConnectivityChecker through SkillStartup DI to Plugin.Instance

18 unit tests covering all new methods (all pass, full suite 814/814 green)

Simplify review applied: thread safety fix, noop override, change detection guard
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
