---
id: JF-85
title: Add cache hit/miss metrics to RequestCounters
status: Done
assignee: []
created_date: '2026-05-06 19:22'
updated_date: '2026-05-06 21:43'
labels:
  - observability
milestone: m-2
dependencies: []
references:
  - claudedocs/research_alexa_best_practices_2026-05-06.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add `CacheHits` and `CacheMisses` counters to `RequestCounters` and increment them in `CachedSearchAsync` (BaseHandler.cs). Currently the cache logs warnings on fallback but doesn't track hit/miss rates, making it impossible to tune TTL or identify when the cache is ineffective.

Expose these metrics in the existing `/diagnostics/metrics` endpoint.

Files: `Diagnostics/RequestCounters.cs`, `Alexa/Handler/BaseHandler.cs`, `Controller/DiagnosticsController.cs`.

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 3.3
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 CacheHits and CacheMisses counters added to RequestCounters
- [ ] #2 CachedSearchAsync in BaseHandler increments appropriate counter on each call
- [ ] #3 Counters exposed in /diagnostics/metrics endpoint response
- [ ] #4 Thread-safe counter implementation (Interlocked or ConcurrentDictionary)
- [ ] #5 Unit tests verify counter increments on hit and miss paths
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added thread-safe CacheHits and CacheMisses counters to RequestCounters using Interlocked. CachedSearchAsync in BaseHandler increments miss on successful queries and hit on cache-fallback. Plugin.Instance exposes RequestCounters (wired via SkillStartup DI). /diagnostics/metrics endpoint now includes CacheHits, CacheMisses, and CacheHitRate. 7 new unit tests cover initial state, increment, accumulation, and concurrent thread safety. All 693 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
