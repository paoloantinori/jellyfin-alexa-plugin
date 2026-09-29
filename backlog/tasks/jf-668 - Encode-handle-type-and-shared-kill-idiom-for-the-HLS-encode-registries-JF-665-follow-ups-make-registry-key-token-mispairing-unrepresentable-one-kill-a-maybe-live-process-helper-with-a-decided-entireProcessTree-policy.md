---
id: JF-668
title: >-
  Encode-handle type and shared kill idiom for the HLS encode registries (JF-665
  follow-ups: make registry/key/token mispairing unrepresentable; one
  kill-a-maybe-live-process helper with a decided entireProcessTree policy)
status: In Progress
assignee: []
created_date: '2026-09-29'
updated_date: '2026-09-29 13:49'
labels:
  - playback-speed
  - encode-gate
  - refactoring
dependencies: []
references:
  - >-
    backlog/tasks/jf-665 -
    Registration-side-of-the-same-key-speed-encode-re-register-race-orphaned-displaced-ffmpeg-at-the-registry-overwrite-unchecked-TryAdd-key-only-encode-flag-clear.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-29 same-turn from the JF-665 /simplify round (4 angles; the altitude angle's two minor findings were judged defensible to defer under JF-665's minimal-change constraint and are tracked HERE so the deferral has an owner).

1. ENCODE HANDLE TYPE (mispairing is currently representable). JF-665 made the three active-encode flag registries generation-aware: `MarkEncodeActive(registry, key)` returns an opaque token and every clear is `ClearEncodeFlag(registry, key, token)` (compare-and-remove). The (registry, key, token) triple travels as PARALLEL parameters through `WaitForFirstSegmentOrKillAsync`, `KillEncodeAndClearFlag`, `StartHlsMonitor`, and `MonitorFfmpegHlsAsync`, so a future caller that pairs the right key with the wrong registry or a stale token gets a SILENT no-op clear, i.e. a silently stuck active-encode flag, which is precisely the failure family JF-665 exists to prevent. Today that hazard is guarded only by doc comments. Fix direction: `MarkEncodeActive` returns a small encode handle bundling registry + key + token whose `Clear()`/`KillAndClear()` are the only clear paths, deleting the extra parameter at each layer and making mispairing unrepresentable. Same diff size as the current threading if done before the next caller appears.

2. SHARED KILL IDIOM + KILL-TREE POLICY. The "kill a maybe-live process" block (HasExited guard, Information log, Kill, catch-race LogDebug) now exists in two registry-kill shapes: `KillSupersededSpeedEncodes` (JF-636) and the displacement kill inside `RegisterLiveSpeedEncode` (JF-665); a third variant lives in the monitor's stall kill (`MonitorFfmpegHlsAsync`). The three have already drifted on one detail: the stall kill uses `Kill(entireProcessTree: true)` while both registry kills use plain `Kill()`. Extract one `TryKillLiveProcess(process, reason)` helper owning the idiom, and DECIDE the tree-vs-plain policy in it (the drift is not a reported bug: the JF-636 supersede test asserts the sh pid only; but the next kill-semantics change must not have to be found in three places).

3. EARLIER DISPLACEMENT KILL (close the first-segment-wait window; from the JF-665 code-review round). The displacement kill fires at RegisterLiveSpeedEncode time, i.e. only after the NEW encode's first-segment wait succeeds. In the window before that (typically sub-second, worst case the ~20s WaitForFirstSegmentOrKillAsync ceiling) the abandoned prior ffmpeg keeps writing BY PATH into the RECREATED directory: its post-eviction segment opens and stream.m3u8 rewrites resolve by path at open time, so both processes can truncate each other's seg_NNNN.ts names, and the new encode's liveness proof can even be satisfied by the prior encode's seg_0000.ts. Pre-JF-665 this window was the prior encode's ENTIRE remaining run; JF-665 bounded it to the first-segment wait. Fix direction: also kill the same-key displaced registry entry at the SupersedeStaleEncodes position inside the per-key lock, BEFORE the new ffmpeg starts (the same abandoned-by-construction argument applies: reaching the encode branch with a live same-key entry requires the mid-encode eviction), keeping the registration-time displacement kill as the backstop. Needs its own red-green pin.

Non-goals (deliberately out): generalizing the speed-encode process registry to the episode-remux/episode-audio/song/audiobook paths (the JF-665 scope note documents that a displaced prior encode on those paths is flag-safe but not process-safe); any behavior change to the kill semantics without a live incident or a test proving the need.

VERIFICATION: existing JF-647/JF-665 pins stay green unchanged (they pin behavior, not signatures); the suite green both TFMs.
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
