---
id: JF-7
title: Implement SkillStartup.StopAsync and fix resource management
status: Done
assignee: []
created_date: '2026-04-29 21:25'
updated_date: '2026-04-29 21:47'
labels: []
milestone: m-1
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`EntryPoints/SkillStartup.cs` has a critical incomplete implementation:

**Line 172-175:** `StopAsync()` method is empty with a TODO: "Is there a way to stop the task?"
**Line 29-35:** Implements IDisposable but doesn't properly manage resources
**Line 75:** Comment indicates CloseHandle should be called but isn't

**Implementation requirements:**
1. StopAsync should cancel the running background task via CancellationToken
2. Store the CancellationTokenSource and Task references properly
3. Properly dispose HttpClient and other resources
4. Log when the service is stopping
5. Handle graceful shutdown with timeout

**Current pattern:** The StartAsync creates a long-running task but has no way to cancel it. Fix by:
- Store CancellationTokenSource as a field
- Pass token to Task.Run
- In StopAsync, cancel the token and wait for task completion with timeout
- Implement proper Dispose pattern
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 StopAsync properly cancels the background task
- [ ] #2 CancellationTokenSource stored and managed correctly
- [ ] #3 Graceful shutdown with configurable timeout
- [ ] #4 Resources (HttpClient, etc.) properly disposed
- [ ] #5 Stop/start lifecycle tested
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented proper StopAsync with CancellationTokenSource cancellation and task awaiting. Removed dead Component/IntPtr handle boilerplate. Added cancellation checks in user iteration loop. Dispose now properly cancels and disposes the CTS. 4 lifecycle tests cover stop-without-start, double dispose, and cancelled token scenarios.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
