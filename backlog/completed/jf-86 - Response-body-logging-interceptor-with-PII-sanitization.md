---
id: JF-86
title: Response body logging interceptor with PII sanitization
status: Done
assignee: []
created_date: '2026-05-06 19:22'
updated_date: '2026-05-06 21:56'
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
Add a response interceptor that logs the response body at DEBUG level with PII sanitization (strip `apiAccessToken`, truncate full `userId`). The controller already logs request bodies at DEBUG, but responses are not logged — making it difficult to debug malformed responses in production.

This should be implemented as an `IResponseInterceptor` in the pipeline, logging after handler execution.

Files: New interceptor in `Alexa/Pipeline/`, registration in `RequestPipeline.cs`.

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 3.2
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 New ResponseLoggingInterceptor created in Alexa/Pipeline/
- [ ] #2 Logs response body at DEBUG level after handler execution
- [ ] #3 PII sanitization: strips apiAccessToken and truncates userId
- [ ] #4 Interceptor registered in RequestPipeline with correct ordering
- [ ] #5 Unit test verifies sanitization of sensitive fields
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
