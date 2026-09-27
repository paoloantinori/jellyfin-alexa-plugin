---
id: JF-647
title: >-
  JF-647 - speed-encode exit watcher can delete a LIVE registry entry (stale
  unconditional TryRemove after a same-key re-register): abandoned ffmpeg holds
  an encode-gate slot
status: To Do
assignee: []
created_date: '2026-09-27 08:16'
labels:
  - playback-speed
  - encode-gate
  - race
dependencies: []
references:
  - >-
    backlog/tasks/jf-637 -
    JF-636-follow-ups-consolidate-the-variant-HLS-machinery-the-JF-632-gate-preamble-and-the-slot-resolution-walk.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-637 review round (the worker's bugs-noticed list; code is JF-636-era, moved verbatim by the JF-637 refactor).

THE RACE: RegisterLiveSpeedEncode's exit watcher (VideoAudioController.cs) polls Process.HasExited at 1s intervals and, on exit, unconditionally TryRemoves the cache key from the active-encode registry. If the OLD encode exits and a NEW same-cache-key encode registers inside that window (a user re-requesting the same speed stream), the stale watcher deletes the LIVE entry. Consequence: KillSupersededSpeedEncodes later degrades to the conservative no-kill path (the registry no longer shows the old process), so an abandoned ffmpeg runs to completion holding one of the two encode-gate slots (MaxConcurrentFfmpegEncodes=2): with two abandoned encodes the gate is fully consumed and new variant-HLS requests stall until they finish.

FIX DIRECTION: the watcher must remove ONLY the entry it registered: key the registry by a generation/token (e.g. store the Process reference and TryRemove only when the stored process is still THIS process, the standard compare-and-remove), instead of unconditional key removal.

VERIFICATION: a unit/integration test that simulates old-exit + new-register interleaving and asserts the new entry survives; the existing superseded-encode tests stay green.
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
