---
id: JF-25
title: Fix ProgressiveResponse HttpClient reuse bug
status: Done
assignee: []
created_date: '2026-05-03 06:34'
updated_date: '2026-05-03 07:01'
labels:
  - bug
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The static `Plugin.HttpClient` is shared across all HTTP operations. When `BaseHandler.SendProgressiveResponse` creates a `ProgressiveResponse`, it tries to set `BaseAddress` on this already-started HttpClient, causing `System.InvalidOperationException: This instance has already started one or more requests. Properties can only be modified before sending the first request.`

Fix by either:
1. Creating a separate HttpClient for progressive responses
2. Passing the base address differently (e.g., via the request URL instead of setting BaseAddress)
3. Using an HttpClient factory pattern

The progressive response feature is non-critical (music still plays) but provides UX value by showing "Searching your library..." while processing.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed by replacing the shared static HttpClient with a fresh HttpClient per progressive response call. The Alexa.NET ProgressiveResponse constructor sets BaseAddress on the passed HttpClient, which throws InvalidOperationException after the first request. Removed unused SharedHttpClient field. Added regression test verifying multiple progressive response calls succeed. All 311 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
