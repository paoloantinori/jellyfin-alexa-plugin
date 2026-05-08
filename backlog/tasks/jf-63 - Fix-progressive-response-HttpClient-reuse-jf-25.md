---
id: JF-63
title: Fix progressive response HttpClient reuse (jf-25)
status: Done
assignee: []
created_date: '2026-05-03 13:39'
updated_date: '2026-05-03 17:07'
labels:
  - bug
  - resilience
  - architecture
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Fix the progressive response HttpClient reuse issue. Already tracked as backlog task jf-25.

The progressive response mechanism creates new HttpClient instances on each call instead of reusing a shared client. This can lead to socket exhaustion under load and violates .NET best practices for HttpClient usage.

Implementation: Inject IHttpClientFactory and use a named/typed client for progressive response calls. Ensure the HttpClient is properly registered in DI via IPluginServiceRegistrator.

Note: This is a duplicate/reference of existing task jf-25. Consolidate with that task.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Duplicate of JF-25 which was already completed. The progressive response HttpClient reuse bug was fixed by creating a fresh HttpClient per progressive response call instead of sharing the static Plugin.HttpClient.
<!-- SECTION:FINAL_SUMMARY:END -->
