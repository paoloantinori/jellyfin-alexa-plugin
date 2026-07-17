---
id: JF-320
title: >-
  Reliability: Fix VideoAudioCache ref-counted semaphore TOCTOU
  (ObjectDisposedException) and atime-based LRU
status: To Do
assignee: []
created_date: '2026-07-12 14:59'
updated_date: '2026-07-13 20:17'
labels:
  - concurrency
  - reliability
  - cache
milestone: m-8
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/VideoAudioCache.cs:134'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/VideoAudioCache.cs:250'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two cache defects from the 2026-07-12 architecture review:

1. TOCTOU in the ref-counted per-item lock. `VideoAudioCache.cs:134-141` does `GetOrAdd` → `Interlocked.Increment` → `WaitAsync`, while `ReleaseLock` (~605-618) does `Decrement` → `TryRemove` → `Dispose()`. A releasing thread can dispose the `SemaphoreSlim` between another thread's `GetOrAdd` and its `WaitAsync`, throwing `ObjectDisposedException`. SUSPECTED (classic flaw in this pattern; narrow window, needs concurrent plays of the same item). Fix: re-check membership after increment, or retry `GetOrAdd` when the fetched entry was disposed.

2. LRU eviction ordered by `LastAccessTimeUtc` (`VideoAudioCache.cs:250,265,272`), i.e. filesystem atime. Linux servers commonly mount with `relatime`/`noatime`, so atime barely updates and 'LRU' degenerates to near-creation-time order — potentially evicting the actively-played audiobook first. Fix: track access time in memory on cache hits (touch-on-serve), or evict by mtime with an explicit touch when serving.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Concurrent plays of the same item cannot throw ObjectDisposedException from the cache lock (membership re-checked or GetOrAdd retried after dispose)
- [ ] #2 Cache eviction order reflects real recency of use, independent of filesystem atime/relatime/noatime mount options
- [ ] #3 The actively-playing item is not a preferred eviction candidate under memory pressure
- [ ] #4 Unit tests cover the lock race (or documented reasoning) and the recency-ordering logic
- [ ] #5 Existing audiobook/stream cache behavior (segment reuse, ?start=) is unaffected
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
