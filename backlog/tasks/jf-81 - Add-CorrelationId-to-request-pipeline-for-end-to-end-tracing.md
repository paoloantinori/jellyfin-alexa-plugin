---
id: JF-81
title: Add CorrelationId to request pipeline for end-to-end tracing
status: Done
assignee: []
created_date: '2026-05-06 19:20'
updated_date: '2026-05-06 20:33'
labels:
  - observability
  - high-priority
milestone: m-2
dependencies: []
references:
  - claudedocs/research_alexa_best_practices_2026-05-06.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add a short CorrelationId (8-char GUID) to the request pipeline so all log statements within a single request flow share a traceable identifier. Currently the controller uses BeginScope with RequestId/UserId/DeviceId, but individual handlers don't carry this context forward consistently.

Implementation:
- Create a `CorrelationIdRequestInterceptor` that generates `context.CorrelationId = Guid.NewGuid().ToString("N")[..8]`
- Add locale and intent name to the logging scope so every log statement within a handler automatically includes these fields
- Ensure the CorrelationId flows through to all handler log statements

Files: New interceptor in `Alexa/Pipeline/`, update `RequestContext` or equivalent pipeline context, update `RequestPipeline.cs` registration.

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 3.1
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 CorrelationIdRequestInterceptor generates 8-char correlation ID per request
- [x] #2 CorrelationId is included in logging scope for all handler log statements
- [x] #3 Locale and intent name are also added to the logging scope
- [x] #4 Existing pipeline tests pass with new interceptor registered
- [x] #5 No performance impact (ID generation is negligible)
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
LoggingRequestInterceptor generates 8-char correlation ID per request (Guid.NewGuid().ToString("N")[..8])

RequestContext.IntentName and RequestContext.Locale computed properties eliminate duplicated extraction logic

RequestPipeline wraps handler execution in ILogger.BeginScope with CorrelationId, Intent, and Locale

All log statements include corr={CorrelationId} for tracing

5 new tests: CorrelationId defaults empty, interceptor sets 8-char hex ID, unique IDs per request, response interceptor access, full pipeline flow
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->
