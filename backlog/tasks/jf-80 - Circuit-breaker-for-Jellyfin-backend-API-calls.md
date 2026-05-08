---
id: JF-80
title: Circuit breaker for Jellyfin backend API calls
status: Done
assignee: []
created_date: '2026-05-06 19:20'
updated_date: '2026-05-06 20:20'
labels:
  - resilience
  - high-priority
milestone: m-2
dependencies: []
references:
  - claudedocs/research_alexa_best_practices_2026-05-06.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement a lightweight circuit breaker that tracks consecutive Jellyfin API failures and short-circuits when the backend is confirmed down. When the Jellyfin server is completely unavailable, every request currently wastes ~3.5s on doomed retries (500ms + 1s + 2s), eating into the 8-second Alexa timeout.

Design:
- Track consecutive failures in a `ConcurrentDictionary` keyed by server URL
- After 5 consecutive failures within 60 seconds → OPEN state (immediately return "server unavailable" without API calls)
- After 30 seconds in OPEN → HALF-OPEN (allow one test request)
- Test succeeds → CLOSED; fails → reset OPEN timer

Files: New `CircuitBreaker.cs` in `Alexa/` or `Alexa/Pipeline/`, integration into handler call path (e.g., `BaseHandler.cs` or request pipeline).

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 2.2
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 CircuitBreaker class with CLOSED/OPEN/HALF_OPEN states per server URL
- [ ] #2 5 consecutive failures within 60s triggers OPEN state
- [ ] #3 30s timeout in OPEN transitions to HALF_OPEN
- [ ] #4 HALF_OPEN allows one probe request; success → CLOSED, failure → back to OPEN
- [ ] #5 Integration point in request pipeline or BaseHandler that checks circuit state before API calls
- [ ] #6 Thread-safe implementation using ConcurrentDictionary
- [ ] #7 Unit tests covering all state transitions
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented circuit breaker pattern with per-server state tracking (Closed/Open/HalfOpen). CircuitBreaker.cs with thread-safe ConcurrentDictionary, CircuitBreakerInterceptor for fail-fast in request pipeline, success/failure recording in BaseHandler.HandleRequestAsync, DI wiring in SkillStartup, "ServerUnavailable" locale strings for all 12 locales, and 11 unit tests covering all state transitions.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
