---
id: JF-110
title: Audit and document SkillStartup fire-and-forget UpdateSkillAsync
status: Done
assignee: []
created_date: '2026-05-09 20:21'
updated_date: '2026-05-10 06:32'
labels:
  - bug
  - investigation
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
In `EntryPoints/SkillStartup.cs`, the `UpdateSkillAsync` call is discarded with `_ =` inside a lambda wrapped by `AlexaUtil.CallAsync`. The outer wrapper returns `Task.FromResult<object?>(null)` immediately, so the update runs unobserved. Any exception from `UpdateSkillAsync` is silently swallowed.

Either:
1. If fire-and-forget is intentional, add a comment explaining why and consider adding `.ContinueWith(t => logger.LogError(...))` for error observation
2. If the update should be awaited, fix the lambda to properly await the call

This was pre-existing behavior (not introduced by the warning fix), but was flagged during code review.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed fire-and-forget UpdateSkillAsync call in SkillStartup.cs.

The old code discarded the task with `return Task.FromResult<object?>(null)` inside a lambda passed to `AlexaUtil.CallAsync`, so:
1. `CallAsync` returned immediately without awaiting the actual SMAPI update
2. Exceptions from `UpdateSkillAsync` were silently swallowed
3. `CallAsync`'s token-refresh retry logic could never fire

Fix: Changed the lambda to `async () => { await UpdateSkillAsync(...); return null; }` so the await propagates through `CallAsync`, enabling proper error observation and auth-retry behavior.

Build: 0 errors. Tests: 983 passed.
<!-- SECTION:FINAL_SUMMARY:END -->
