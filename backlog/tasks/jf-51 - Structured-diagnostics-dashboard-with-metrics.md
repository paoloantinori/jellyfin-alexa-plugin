---
id: JF-51
title: Structured diagnostics dashboard with metrics
status: Done
assignee: []
created_date: '2026-05-03 13:38'
updated_date: '2026-05-03 21:13'
labels:
  - enhancement
  - diagnostics
  - monitoring
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add structured diagnostics dashboard exposing request metrics via REST API for admin monitoring. Inspired by the Meilisearch Jellyfin plugin's status endpoints.

Currently there's no visibility into how the skill is performing - which intents are used most, how fast they respond, or how often they fail.

Implementation:
1. Extend the existing DiagnosticsController with metrics endpoints
2. Track per-intent: request count, average response time, error count, last error message
3. Expose via GET endpoint (e.g., /Diagnostics/Metrics) returning JSON
4. Optionally add a simple dashboard section to the config page showing top intents and health status
5. Already have RequestCountersTests.cs - expand the underlying counters infrastructure
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 GET /alexaskill/api/diagnostics/metrics returns per-intent request counts, error counts, and average response times
- [x] #2 GET /alexaskill/api/diagnostics/health returns a simple health check (uptime, total requests, error rate)
- [x] #3 Existing RequestCounters class is extended to track per-intent timing and errors
- [x] #4 Unit tests cover the new metrics tracking and API endpoints
- [x] #5 No breaking changes to existing diagnostics endpoints
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Extended RequestCounters with per-intent timing (avg/min/max response time), error tracking, and uptime. Added MetricsResponseInterceptor pipeline interceptor for automatic per-request timing. Added /diagnostics/metrics endpoint (sorted per-intent stats) and /diagnostics/health endpoint (with error-rate-based degradation). 10 new unit tests covering counters, interceptor, and thread safety. All 529 tests pass (1 pre-existing failure, 1 skipped).
<!-- SECTION:FINAL_SUMMARY:END -->
