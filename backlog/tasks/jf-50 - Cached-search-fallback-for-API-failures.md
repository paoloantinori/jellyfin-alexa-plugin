---
id: JF-50
title: Cached search fallback for API failures
status: Done
assignee: []
created_date: '2026-05-03 13:38'
updated_date: '2026-05-03 18:45'
labels:
  - enhancement
  - resilience
  - offline
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement cached fallback for search operations when the Jellyfin API is temporarily unavailable. Inspired by the Gelato plugin's decorator pattern for core services.

When a media search fails due to a transient Jellyfin API error, instead of immediately returning an error to the user, serve cached results from the last successful search for the same or similar query.

Implementation:
1. Cache successful search results per user (in-memory with size limit, e.g., last 100 queries)
2. On search failure: check cache for exact or similar matches
3. If cache hit: return cached results with a hint ("Here's what I found earlier...")
4. If cache miss: return the normal error message
5. Use the decorator pattern (like Gelato) to wrap the search service transparently
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added SearchResultCache — thread-safe ConcurrentDictionary-based per-user cache with size limits, time-based expiration, and Noop fallback. Added CachedSearchAsync to BaseHandler that caches successful results and serves cached fallback on API failure. Wired through DI in Registrator and SkillStartup.
<!-- SECTION:FINAL_SUMMARY:END -->
