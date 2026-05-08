---
id: JF-48
title: Interceptor-based playback state persistence
status: Done
assignee: []
created_date: '2026-05-03 13:38'
updated_date: '2026-05-03 17:00'
labels:
  - enhancement
  - resilience
  - architecture
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement interceptor/middleware-based persistence for playback state, replacing manual per-handler persistence calls. Inspired by the official Amazon ASK audio player sample's LoadPersistenceRequestInterceptor/SavePersistenceResponseInterceptor pattern.

Currently each handler manually loads and saves playback state. This is error-prone (easy to forget to save) and creates repetitive code.

Implementation: Create ASP.NET middleware or request/response pipeline that:
1. Before each request: loads playback state (current track, offset, queue position) from Jellyfin session storage
2. After each response: automatically saves any modified playback state
3. Eliminates manual persistence calls in individual handlers

This ensures state is always consistent and reduces boilerplate across all intent handlers.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented request/response interceptor pipeline pattern (IRequestInterceptor, IResponseInterceptor, RequestContext, RequestPipeline). Includes logging interceptors for timing, session attribute preservation interceptor for multi-turn conversations. Response interceptors run in reverse registration order. Fixed Alexa.NET namespace collision (Request type is in Alexa.NET.Request.Type, not Alexa.NET.Request). Added 28 unit tests covering all pipeline components. /simplify review cleaned up redundant allocations, duplicate logging, and simplified SessionAttributesInterceptor merge logic.
<!-- SECTION:FINAL_SUMMARY:END -->
