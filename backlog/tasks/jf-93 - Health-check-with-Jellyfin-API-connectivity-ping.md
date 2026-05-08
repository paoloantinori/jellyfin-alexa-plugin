---
id: JF-93
title: Health check with Jellyfin API connectivity ping
status: Done
assignee: []
created_date: '2026-05-06 19:23'
updated_date: '2026-05-06 22:21'
labels:
  - observability
milestone: m-2
dependencies: []
references:
  - claudedocs/research_alexa_best_practices_2026-05-06.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Enhance the health check endpoint to verify actual Jellyfin API connectivity, not just configuration validity. Currently `DiagnosticsController.GetHealth()` checks configuration and token expiration but does NOT verify that the Jellyfin server is actually reachable.

Add a lightweight "ping" — single `GET /System/Info` call to the configured Jellyfin server. If it fails or times out (>2s), mark health as `Degraded`. Cache the check result for 30 seconds to avoid overhead on every health poll.

Files: `Controller/DiagnosticsController.cs`, potentially a new `JellyfinHealthCheck.cs` helper.

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 3.4
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 DiagnosticsController.GetHealth() includes a lightweight GET /System/Info call to configured Jellyfin server
- [ ] #2 Health check returns Degraded if API call fails or exceeds 2s timeout
- [ ] #3 API connectivity result cached for 30s to avoid overhead on every health poll
- [ ] #4 Cache invalidates on config changes (server URL/token update)
- [ ] #5 Existing health checks (config validation, token expiration) still pass
- [ ] #6 Unit tests with mocked HttpClient for success/failure/timeout scenarios
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added JellyfinConnectivityChecker singleton with GET /System/Info/Public ping, 2s timeout, 30s cache with server-address-based invalidation. Uses SemaphoreSlim for async-safe concurrency. Health endpoint includes JellyfinConnectivity status and downgrades to Degraded when unreachable. 4 unit tests added.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
