---
id: JF-15
title: Convert handler pipeline to async and eliminate .Result blocking
status: Done
assignee: []
created_date: '2026-05-01 06:02'
updated_date: '2026-05-01 07:01'
labels:
  - robustness
  - architecture
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
HIGH: The entire handler pipeline uses synchronous .Result/.Wait() on async methods, causing potential deadlocks, thread pool starvation, and 8-second Alexa timeout risks.

Changes needed:
- BaseHandler.HandleRequest() -> async Task<SkillResponse> HandleRequestAsync()
- BaseHandler.Handle() -> async Task<SkillResponse> HandleAsync()
- SmapiManagement: all methods to async (await instead of .Result)
- AlexaUtil.Call<T>() to async
- LwaClient methods to async
- SkillStartup.StartAsync to use await instead of Task.Run + .Result
- Add CancellationToken support with 6-second timeout (2s buffer for Alexa 8s deadline)

This is a prerequisite for progressive responses and proper resilience patterns.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Async Conversion Plan

### Phase 1: Core method signatures
1. BaseHandler.HandleRequest() → HandleRequestAsync() (await GetSessionByAuthenticationToken)
2. BaseHandler.Handle() abstract → HandleAsync() returning Task<SkillResponse>

### Phase 2: All concrete handlers (29 classes)
- Update Handle() → HandleAsync() with async/await
- Handlers that call async methods (SessionManager, etc.) get proper await
- Simple handlers just wrap return in Task.FromResult

### Phase 3: Utility classes
- AlexaUtil.Call<T>() → CallAsync<T>() (await RefreshDeviceToken)
- SmapiManagement: all 6 methods to async (CreateSkill, UpdateSkill, GetSkill, DeleteSkill, GetAccountLinkData, GetSkillStatus + WaitForSkillStatusAsync)

### Phase 4: Entry points
- AlexaSkillController.HandleIntentRequest → async (await handler pipeline)
- SkillStartup.StartAsync → remove Task.Run, use await directly

### Phase 5: CancellationToken + timeout
- Add CancellationToken to HandleRequestAsync with 6s timeout (2s buffer for Alexa 8s deadline)
- Thread through to handlers
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Converted the entire handler pipeline from synchronous .Result/.Wait() blocking to proper async/await:

**Core changes:**
- `BaseHandler.HandleRequest()` → `HandleRequestAsync()` with `await GetSessionByAuthenticationToken()`
- `BaseHandler.Handle()` abstract → `HandleAsync()` returning `Task<SkillResponse>` with `CancellationToken`
- 32 concrete handlers updated to `HandleAsync` with proper `await` for async calls (SessionManager.OnPlayback*, etc.)
- `AlexaUtil.Call<T>()` → `CallAsync<T>()` accepting `Func<Task<T>>`, properly awaiting token refresh
- `SmapiManagement`: all 6 methods converted to async (CreateSkillAsync, UpdateSkillAsync, GetSkillAsync, DeleteSkillAsync, GetAccountLinkDataAsync, GetSkillStatusAsync)
- `AlexaSkillController.HandleIntentRequest` → async with 6-second CancellationToken (2s buffer for Alexa 8s deadline)
- `SkillStartup.StartAsync` → removed Task.Run wrapper, uses proper await
- `LWAController` → converted Thread to Task.Run with async lambda
- `ConfigurationController.DeleteUserSkill` → async with TryDeleteCloudSkillAsync

**Files changed:** 38 files (32 handlers + BaseHandler + AlexaUtil + SmapiManagement + AlexaSkillController + SkillStartup + LWAController + ConfigurationController + 5 test files)

**Verification:** Build 0 errors, 130/130 tests pass
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
