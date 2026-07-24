---
id: JF-320
title: >-
  Reliability: Fix VideoAudioCache ref-counted semaphore TOCTOU
  (ObjectDisposedException) and atime-based LRU
status: Done
assignee:
  - claude
created_date: '2026-07-12 14:59'
updated_date: '2026-07-19 19:35'
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
- [x] #1 Concurrent plays of the same item cannot throw ObjectDisposedException from the cache lock (membership re-checked or GetOrAdd retried after dispose)
- [x] #2 Cache eviction order reflects real recency of use, independent of filesystem atime/relatime/noatime mount options
- [x] #3 The actively-playing item is not a preferred eviction candidate under memory pressure
- [x] #4 Unit tests cover the lock race (or documented reasoning) and the recency-ordering logic
- [x] #5 Existing audiobook/stream cache behavior (segment reuse, ?start=) is unaffected
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

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-19 09:53
---
PART 1 (TOCTOU) DONE (2026-07-19, commit 7e26f22): LockItemAsync now wraps WaitAsync in a retry loop catching ObjectDisposedException -- if a ReleaseLock disposes the semaphore between GetOrAdd and WaitAsync, the loop re-acquires a fresh entry. Documented reasoning (the race is narrow/non-deterministic; a deterministic unit test isn't practical, per AC#4's 'or documented reasoning'). Full suite 2534/2534. PART 2 (atime LRU) DEFERRED: the eviction (VideoAudioCache.cs:334) orders by filesystem LastAccessTimeUtc, which degenerates under relatime/noatime mounts. The fix (in-memory access tracking touched at GetCachedFile/GetCachedHlsPlaylist/FindSegmentPath + used in eviction with atime fallback) is moderate + bounded-risk but warrants its own focused implementation + a recency-ordering test; not rushed as an addendum. Task stays In Progress until part 2 lands (or part 2 is split into its own task).
---

created: 2026-07-19 19:35
---
PART 2 (LRU) DONE + LEAK FIXES (commits f78b805 + 62ae664). The atime-based LRU (which degenerates under relatime/noatime mounts) is replaced by in-memory recency: a _lastAccessUtc ConcurrentDictionary touched on every cache serve (GetCachedFile hits, GetCachedHlsPlaylist hit, FindSegmentPath per-segment hits), with eviction preferring the in-memory time (atime fallback for unrecorded entries -- no regression) + TryRemove on evict. Test: a recently-served file survives eviction even when its atime is older than a cold file's (fails without the in-memory recency). /code-review high (opus): fundamentally correct -- TOCTOU retry (no livelock, no RefCount leak), LRU key matching consistent, FindSegmentPath restructure equivalent, atime-fallback preserves prior behavior, snapshot consistency OK. Found + fixed 2 slow memory-leak paths: (1) Cleanup(itemId) deleted files/dirs but left _lastAccessUtc orphans; (2) the eviction TryRemove sat after File.Delete so a delete-failure orphaned it. Both fixed (62ae664): Cleanup TryRemoves each path; eviction TryRemove moved after the try/catch. Full suite 2537/2537. DEPLOY: f78b805 (LRU) deployed + sanity-verified; 62ae664 (leak fixes) deploy PENDING a classifier/Bash outage -- will deploy when tooling recovers (the leak fixes are minor -- slow orphan accumulation; not urgent). JF-320 complete.
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Two cache reliability defects fixed. PART 1 (TOCTOU): LockItemAsync wraps WaitAsync in a retry loop catching ObjectDisposedException, so a ReleaseLock disposing the semaphore between GetOrAdd and WaitAsync no longer crashes concurrent same-item plays. PART 2 (LRU): in-memory _lastAccessUtc recency tracking (touched on every serve) replaces the atime-based eviction that degenerated under relatime/noatime mounts, with an atime fallback so unrecorded entries keep prior behavior; evicted/cleaned entries are removed from the map. /code-review high (opus): fundamentally correct; found + fixed 2 slow memory-leak paths (Cleanup orphans + eviction delete-failure orphan). Tests: TOCTOU retry (documented reasoning) + a recency test (served file survives despite stale atime). Full suite 2537/2537. Deployed (part 1 + part 2); leak-fix deploy pending a tooling outage.
<!-- SECTION:FINAL_SUMMARY:END -->
