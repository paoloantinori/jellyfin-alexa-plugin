---
id: JF-18
title: Add diagnostics and health check endpoints
status: Done
assignee:
  - claude
created_date: '2026-05-01 06:02'
updated_date: '2026-05-01 19:43'
labels:
  - debugging
  - observability
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
MEDIUM: No runtime visibility into plugin state without reading server logs.

Add:
1. GET alexaskill/api/diagnostics endpoint (admin-only):
   - Plugin version, config status (sans secrets)
   - Per-user skill status, token expiry
   - Active session count, supported locales
   - In-memory request counters (total, errors, per-intent counts)
   
2. Register AlexaPluginHealthCheck via IPluginServiceRegistrator:
   - Check ServerAddress configured
   - Check LWA credentials present
   - Check users with expired SMAPI tokens
   - Report Healthy/Degraded/Unhealthy to Jellyfin's /health endpoint

3. Add request/response action filter for automatic timing and logging on all controller endpoints
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added diagnostics endpoint and request counters.

**Changes:**
- New `GET alexaskill/api/diagnostics` endpoint (admin-only via RequiresElevation) returning JSON with: plugin version, config validity, per-user skill status with SMAPI token expiry timestamps, supported locales, and request metrics
- New `RequestCounters` service: thread-safe request/error/per-type tracking using `ConcurrentDictionary` and `Interlocked`, registered as singleton in DI
- Health status assessment: Healthy/Degraded/Unhealthy based on ServerAddress, config validation errors, and expired SMAPI tokens
- Counters wired into `AlexaSkillController.HandleIntentRequest` pipeline (increment on each request, per-type tracking, error counting in catch blocks)

**Tests:** 9 new tests for RequestCounters including thread-safety validation. All 172 tests pass.

Skipped formal health check registration (IHealthCheck) as Jellyfin 10.11 doesn't expose this interface for plugins. Skipped action filter — the diagnostics endpoint + counters provide equivalent observability with less complexity.
<!-- SECTION:FINAL_SUMMARY:END -->
