---
id: JF-665
title: >-
  Registration side of the same-key speed-encode re-register race: orphaned
  displaced ffmpeg at the registry overwrite, unchecked TryAdd, key-only
  encode-flag clear
status: To Do
assignee: []
created_date: '2026-09-29 01:23'
labels:
  - playback-speed
  - encode-gate
  - race
dependencies: []
references:
  - >-
    backlog/tasks/jf-647 -
    JF-647-speed-encode-exit-watcher-can-delete-a-LIVE-registry-entry-stale-unconditional-TryRemove-after-a-same-key-re-register-abandoned-ffmpeg-holds-an-encode-gate-slot.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-29 same-turn from the JF-647 code-review round (high effort, 4 findings; 2 applied in JF-647: the compare-and-remove at both removal sites, the logger capture; this task tracks the remainder).

JF-647 fixed the REMOVAL half of the same-key speed-encode re-register race (the exit watcher and the supersede kill now remove only the entry they observed). The REGISTRATION half is still unguarded, and it shares one root with a sibling artifact:

1. ORPHANED DISPLACED PROCESS (VideoAudioController.cs RegisterLiveSpeedEncode, the indexer overwrite): when a same-key re-register happens (the variant's cache directory was evicted mid-encode, then a re-request restarted it), the overwrite `_activeAudioSpeedEncodeProcesses[cacheKey] = (new, ...)` silently orphans the still-running PRIOR process: it becomes invisible to every future KillSupersededSpeedEncodes pass (which reads only the registry), is never killed, and runs to completion holding one of the two MaxConcurrentFfmpegEncodes slots. Exit-order variant leaks the same way: if the newest generation exits first, its watcher clears the key while older generations still run. Fix direction: at overwrite (or on exit-order mismatch), kill the displaced still-live process; by construction it encodes a variant whose cache the new encode is rebuilding, i.e. it is abandoned.

2. SIBLING ARTIFACT ON THE SHARED ENCODE-FLAG REGISTRY (both variant-HLS paths use _activeEpisodeEncodes, so the episode variant is equally exposed):
   a. ServeVariantHlsAsync's `_activeEpisodeEncodes.TryAdd(spec.CacheKey, true)` discards its result (the unchecked door that admits a second concurrent same-key encode writing seg_%04d.ts into the same live directory the first is writing).
   b. MonitorFfmpegHlsAsync's finally clears the flag with a key-only TryRemove: the OLD encode's exit clears the flag while the NEW encode still runs, which turns TryHoldForNearAheadSegmentAsync's ContainsKey gate off early so near-ahead segment requests 404 (a device seek errors mid-encode).

3. TEST-DETERMINISM NOTE (from the same round): the JF-647 pin samples wall-clock (Task.Delay asserts) and can false-pass against unfixed code on a >2.6s thread-pool stall. A watcher poll-interval test seam (the SegmentHoldPollInterval / KeyedOneShotDebounce precedent, an internal settable TimeSpan shrunk to milliseconds in tests) would make it deterministic and sub-second. Intentionally NOT added in JF-647 (minimal change; two of three reviewers advised against a production hook in that diff); do it here if items 1-2 are addressed, or standalone.

VERIFICATION: unit tests simulating (a) same-key re-register while the prior process still lives (assert the prior process is killed and the gate slot released), (b) the flag staying set while the newer same-key encode runs (near-ahead hold stays on), (c) the TryAdd rejecting a second same-key admission.
<!-- SECTION:DESCRIPTION:END -->

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
