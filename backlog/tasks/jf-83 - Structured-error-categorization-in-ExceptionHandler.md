---
id: JF-83
title: Structured error categorization in ExceptionHandler
status: Done
assignee: []
created_date: '2026-05-06 19:20'
updated_date: '2026-05-06 21:19'
labels:
  - observability
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
Define error categories in `ExceptionHandler.cs` (and propagate through `BaseHandler.cs`) so that different error types produce appropriate user-facing responses, logging levels, and diagnostic signals.

Proposed categories:
| Category | Example | User Response | Log Level |
|---|---|---|---|
| TransientBackend | Jellyfin timeout, 503 | "Having trouble reaching your server" | Warning |
| PermanentBackend | 404, auth failure | "That media wasn't found" | Error |
| UserError | Empty slots, invalid input | "I couldn't understand that" | Information |
| SkillError | Unhandled exceptions | "Something went wrong [ref]" | Critical |
| Timeout | 8s Alexa limit exceeded | "That took too long" | Warning |

Each category maps to a specific locale string key and logging severity.

Files: `Alexa/Handler/ExceptionHandler.cs`, `Alexa/Handler/BaseHandler.cs`, locale string files in `Alexa/Locale/`.

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 2.4
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 ErrorCategory enum defined with TransientBackend, PermanentBackend, UserError, SkillError, Timeout
- [ ] #2 Each category maps to a locale string key for user-facing messages
- [ ] #3 Each category maps to appropriate log level (Warning/Error/Information/Critical)
- [ ] #4 ExceptionHandler uses categories for structured logging
- [ ] #5 All 12 locale files include new response strings for each category
- [ ] #6 Unit tests verify correct categorization for sample exceptions
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added ErrorCategory enum (TransientBackend, PermanentBackend, UserError, SkillError, Timeout), ErrorClassifier with Classify(Exception) and ClassifyAlexaError(string?), and ErrorCategoryInfo mapping categories to locale keys and log levels. Updated ExceptionHandler to use structured categorization instead of hardcoded "SomethingWrong". Added 12 unit tests. Added 4 new locale strings to all 12 locale files (reused existing ServerUnavailable for TransientBackend). Fixed existing StructuredLoggingTests to match new Critical log level for INTERNAL_ERROR.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
