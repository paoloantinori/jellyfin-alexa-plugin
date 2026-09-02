---
id: JF-449
title: >-
  Playback persistence residual interleavings: Clear can resurrect a deleted
  queue file; tracker final flush can collide on the shared .tmp path
status: To Do
assignee: []
created_date: '2026-09-02 02:41'
updated_date: '2026-09-02 03:58'
labels:
  - playback
  - concurrency
  - tech-debt
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Playback/AudiobookPositionTracker.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/DebouncedLibraryIndexService.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Surfaced by the JF-429 simplify review (2026-09-02); both are PRE-EXISTING interleavings, not regressions from JF-429, and both are benign in the common case.

1) Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs: System.Threading.Timer.Dispose does not wait for an already-started callback. If a per-device debounce timer's PersistDevice callback has already begun when Clear(deviceId) runs, the callback completes after Clear deleted queue_<device>.json and re-creates the file (PersistToDisk serializes the in-memory queue object it captured). The deleted queue comes back on next startup. Window is one debounce period (2s) around a Clear.

2) Jellyfin.Plugin.AlexaSkill/Alexa/Playback/AudiobookPositionTracker.cs: the Dispose final flush (PersistToDisk) and an in-flight debounce timer callback can run concurrently; both write the same tempPath (_dataFilePath + ".tmp") before File.Move. Worst case one write fails (file in use), which is caught and logged, so the flush silently falls back to the previous on-disk content. Timer.Dispose(WaitHandle) style waiting or a per-writer temp name are candidate shapes; decide during implementation.

Scope note: JF-429 already hardened the ARM side (volatile _disposed, in-lock re-check, arm/dispose mutual exclusion via _persistLock/_timerLock). This task is only about callback-vs-teardown interleavings that survive that fix. Do not change debounce delays (3s tracker, 2s queue manager), persistence formats, or Timer.Change reset semantics without a separate decision.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Clear(deviceId) followed by an already-started PersistDevice callback for that device leaves no queue_<device>.json behind (verified by a test that forces the interleaving)
- [ ] #2 AudiobookPositionTracker final flush completes even when an in-flight debounce callback runs concurrently: no swallowed write failure, persisted content is the flushed state
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Sync-review addition 2026-09-02: a third pre-existing window in the same family. DeviceQueueManager.Dispose calls PersistAll() before the timer-teardown lock section, so a debounce timer firing inside that window writes queue_<device>.json concurrently with PersistAll's own non-atomic WriteAllText to the same path. Microsecond window, pre-existing order (JF-429 deliberately kept the old order). Candidate fix shape, same as the tracker's: adopt AudiobookPositionTracker's ordering (timer teardown under the lock first, then the final flush).

2026-09-02 simplify pass (batch JF-429/430/433) landed three related pointers on this task, since its fix re-touches both classes: (1) TEARDOWN DIVERGENCE: the two Dispose protocols already disagree (tracker: flag, then timer teardown under lock, then final flush; DQM: flag, then PersistAll, then teardown). When fixing the interleavings here, adopt ONE defined order in both. (2) SHARED HELPER: the arm-guard idiom now exists 3x (ArmOneShot + both playback copies, near-verbatim); the fix should introduce a small sealed keyed one-shot debounce helper in Alexa/Util/ (Arm/Disarm/DisposeAll owning the volatile flag, lock, timer map) that both classes adopt, so the interleaving fix lands once. The arm-copy itself was judged defensible this round (different key shapes, Timer.Change idiom, no viable base-class derivation). (3) TEST SPEED: the two new race tests sleep 7s wall-clock; the original rejection of debounce test hooks predates the discovery that InternalsVisibleTo("Jellyfin.Plugin.AlexaSkill.Tests") already exists (csproj:15), so an internal ctor overload or internal interval hook crosses no new assembly boundary. Fold that in here: 7s drops under 0.5s.
<!-- SECTION:NOTES:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
