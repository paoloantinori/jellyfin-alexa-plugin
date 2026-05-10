---
id: JF-111
title: Cache Task.FromResult responses for sync handlers on hot path
status: Done
assignee: []
created_date: '2026-05-09 20:21'
updated_date: '2026-05-10 06:35'
labels:
  - performance
  - optimization
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Several handlers that were converted from `async` to synchronous `Task.FromResult` are on the Alexa request hot path (called frequently during playback): `NextIntentHandler`, `PreviousIntentHandler`, `PlayIntentHandler`, `ResumeIntentHandler`.

Each call to `Task.FromResult<SkillResponse>(...)` allocates a boxed `Task` on the heap. For frequently-called handlers, caching the completed task as a static field would eliminate per-request allocations:

```csharp
// Example for a handler that always returns the same response
private static readonly Task<SkillResponse> EmptyResponseTask = 
    Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
```

Note: This only applies to handlers with deterministic, argument-independent responses. Handlers that build responses from parameters cannot be cached. Profile first to confirm this matters in practice.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Investigated and dismissed. .NET 9's `Task.FromResult<T>` already maintains an internal cache of completed tasks, so per-request allocations are minimal. 

Analysis of the hot-path handlers showed that only `PauseIntentHandler` has deterministic responses (`Empty()` or `AudioPlayerStop()`). All other handlers (`NextIntentHandler`, `PreviousIntentHandler`, `PlayIntentHandler`, `ResumeIntentHandler`, `StartOverIntentHandler`) build responses from parameters (stream URLs, item metadata, locale strings) and cannot be cached.

The marginal allocation savings don't justify the complexity of manually caching completed tasks. No code changes needed.
<!-- SECTION:FINAL_SUMMARY:END -->
