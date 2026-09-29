---
id: JF-665
title: >-
  Registration side of the same-key speed-encode re-register race: orphaned
  displaced ffmpeg at the registry overwrite, unchecked TryAdd, key-only
  encode-flag clear
status: Done
assignee: []
created_date: '2026-09-29 01:23'
updated_date: '2026-09-29 07:38'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DONE 2026-09-29 (worktree agent-a51bd62f563628376, not pushed).

PER-FINDING DISPOSITION:

1. ORPHANED DISPLACED PROCESS (the RegisterLiveSpeedEncode overwrite): FIXED, kill-on-overwrite. The gate read settled the tradeoff: StartFfmpegProcessGatedAsync's release is a per-PROCESS 500ms exit-poll (the gate instance is captured locally at acquire and the poll watches that process object), so a displaced encode holds its slot exactly as long as it RUNS; leave-and-log would keep the full JF-647 harm (an abandoned encode burning CPU and a gate slot to completion) with no consumer, because the only route to a same-key re-register is a mid-encode eviction of the prior variant's cache directory (GetCachedHlsPlaylist serves any non-empty playlist, so a second same-key request re-encodes only after that). The displaced process is therefore abandoned by construction. RegisterLiveSpeedEncode now runs a TryAdd/TryUpdate loop: on a taken key it reads the holder, kills the displaced process if still live (Information log, try/catch mirroring the supersede kill, plus a ReferenceEquals guard so a hypothetical same-process double registration cannot kill itself), then swaps atomically; a lost TryUpdate retries against the new holder, so a concurrent same-key registration can never be silently orphaned by the race. Invariant that now holds end to end: every path that drops a LIVE registry entry kills it first (supersede kill, displacement kill), and the exit watcher only ever removes an already-exited process's entry.

2. UNCHECKED TryAdd: FIXED by removing the TryAdd, not by checking it. Read against the code: with the old bool value a failed TryAdd was state-wise a NO-OP (the key stays present with value true either way), which is exactly why ignoring the result was harmless until now; the artifact lived entirely in the KEY-ONLY CLEARS (finding 3). Once the value carries identity, a checked TryAdd would leave the PRIOR generation owning the flag, so the set is now an unconditional indexer replace under a fresh generation token (the new MarkEncodeActive helper): the newest generation always owns the flag. SetEncodeActiveForTest mints through the same helper, so a test can simulate the newer generation of the race. The task's verification item (c) ("the TryAdd rejecting a second same-key admission") maps to pin B below: a second same-key admission now REPLACES ownership instead of being rejected, and the prior generation's late clear can no longer drop it.

3. KEY-ONLY ENCODE-FLAG CLEAR (the monitor's finally): FIXED, generation-aware compare-and-remove at EVERY clear site, not only the finally. The three flag registries (_activeEpisodeEncodes, _activeAudiobookEncodes, _activeVideoAudioEncodes) moved from ConcurrentDictionary<string, bool> to <string, object>; the value is an opaque per-encode token minted by MarkEncodeActive (reference identity IS the generation; the Process itself cannot serve as the token because the episode and variant cores set the flag BEFORE ffmpeg starts, a documented ordering that is untouched). Every clear is now the new ClearEncodeFlag (TryRemove(KeyValuePair), the JF-647 shape): both startup catches around StartFfmpegProcessGatedAsync, KillEncodeAndClearFlag (which also covers the WaitForFirstSegmentOrKillAsync failure path), and MonitorFfmpegHlsAsync's finally (StartHlsMonitor and MonitorFfmpegHlsAsync take the token as a REQUIRED parameter, the JF-536 no-default lesson restated in their docs). The song core declares its token before its prewrite try with a sentinel: a prewrite failure before the mark clears with a token the registry never held (a no-op), where the old key-only catch could have dropped a prior generation's flag. Healthy paths are unchanged: presence semantics (ContainsKey reads) are untouched, the flag-before-ffmpeg ordering is untouched, and a registration with no prior flag behaves identically to the old TryAdd.

DOCUMENTED, NOT FIXED (scope): the episode-remux and episode-audio variants have no process registry (the JF-636 supersede machinery is speed-only), so a displaced prior encode on THOSE paths still runs to completion writing into unlinked inodes after an eviction; it is invisible to every serve path (its directory is gone) and is now flag-safe (its late monitor clear no-ops against the newer generation's token), but it still burns CPU and holds a gate slot until it exits on its own. Killing it would mean generalizing finding 1's process registry to those paths; not done here because the task's finding 1 names the speed registry and the constraint is minimal changes.

PINS (VideoAudioControllerTests, three new; the JF-647 pin untouched and green):
- RegisterLiveSpeedEncode_SameKeyReRegister_KillsDisplacedLiveEncode: seam-driven finding-1 pin; prior process still LIVE at the same-key re-register; asserts it is killed at the swap, the registry names the new process, and the new generation's own watcher still clears on exit.
- MonitorHls_LatePriorGenerationClear_KeepsNewerEncodeFlagSet: gen 1 is a REAL endpoint encode (hung fake ffmpeg, 2s stall budget override so its monitor's finally lands within seconds); gen 2's registration is simulated through the test seam immediately after the endpoint returns (well inside the budget); asserts the flag stays set through a settle window past the STALLED log.
- MonitorHls_OwnGenerationExit_StillClearsEncodeFlag: the healthy-path guard (over-fix protection); the owning generation's monitor clear still fires; its fake ffmpeg lingers 2s before a clean exit so the set-assert cannot race the clear.
- New read probe EncodeActiveForTest (the read twin of SetEncodeActiveForTest).
RED-GREEN PROOF: with the three fixes temporarily reverted (plain indexer overwrite, TryAdd set, key-only clears) pins 1 and 2 FAIL on both TFMs while pin 3 stays green (its role is guarding the healthy path), exactly the expected matrix; with the fixes restored all four seam pins pass on both TFMs.

GATE-RELEASE NOTE: the task's verification item (a) also names asserting the gate slot release; no gate probe was added (it would be a production hook for a test-only read): the gate's exit-poll release is per-process and unconditional on exit, so the KILL is the asserted link and the release follows mechanically.

GATES: /simplify (4 parallel angles on the diff). Efficiency and reuse: no production findings (the reuse angle's one LOW test-side suggestion, a shared KillIfLive test helper for the two adjacent pins' finally blocks, was SKIPPED because it would require modifying the existing JF-647 pin, which the task forbids; both review angles independently judged a two-occurrence helper premature). Simplification: three comment-level fixes APPLIED (the stale "flag TryAdd before the gated start" wording in ServeVariantHlsAsync's doc updated to the MarkEncodeActive/generation-aware-clear phrasing; the incident-tail rationale deduplicated to its canonical home in MarkEncodeActive's doc with the monitor's finally and the registry docs pointing at it; the ReferenceEquals self-kill guard now carries its half-line reason). Altitude: mechanism judged at the right depth; its two named refinements (an encode-handle type bundling registry+key+token so mispairing is unrepresentable; one shared kill-a-maybe-live-process helper with a decided entireProcessTree policy) FILED same-turn as JF-668.

code-review high (5 findings, all dispositioned): F3 (episode-remux/episode-audio displaced encodes not killed, speed-only registry) = the DOCUMENTED scope note above. F4 (kill-idiom duplication + entireProcessTree drift) = JF-668 item 2. F5 (pin B's ~1.6s timing margin on a descheduled runner) = FIXED in the pin: stall budget 2s -> 5s plus a premise assert right after the gen-2 registration (no STALLED log yet), which turns both the flaky-red and the vacuous-green shapes into loud failures. F1 (the flag keying conflates art-tick generations: a newer-ticks encode displacing an older-ticks flag entry, finishing first, and its correct clear letting ValidateEpisodeCacheAsync delete the older-ticks LIVE directory) = INHERITED, not a JF-665 regression (pre-diff BOTH generations' key-only clears dropped the flag in the same scenario; post-diff only the newest generation's clear can), and beyond minimal scope because every presence reader keys by bare key; FILED as JF-669 with the fix directions. F2 (the displacement kill fires only after the new encode's first-segment wait, leaving a worst-case ~20s window in which the abandoned prior ffmpeg writes BY PATH into the recreated directory, its post-eviction segment opens and playlist rewrites resolving by path, and gen-2's liveness proof can even be satisfied by gen-1's seg_0000.ts) = pre-existing exposure class that JF-665 BOUNDED from the prior encode's entire remaining run to the first-segment wait; the earlier-kill refinement (kill the same-key displaced entry at the SupersedeStaleEncodes position inside the lock, before the new ffmpeg starts) FILED as JF-668 item 3 with its own pin requirement rather than grown into this diff.

VERIFICATION TAIL: Release build 0 warnings 0 errors both TFMs on the final tree (TreatWarningsAsErrors on); Debug build identical; the four seam pins (3 new + the unchanged JF-647 pin) green both TFMs after every review-driven edit; FULL suite on the final tree 4730/4730 net9.0 + 4730/4730 net10.0 (4727 baseline + the 3 new pins), exit 0; existing tests unmodified (the only test-file change is the three added pins); no interaction model, locale, or config surface touched (DoD items 4/6/7/8 not applicable to a controller-internal race fix).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-29 as merge 1341cd97 (pushed; C# fix in the streaming controller, deploy pending the JF-666 companion): the registration side of the same-key speed-encode race closed. RegisterLiveSpeedEncode kills the LIVE displaced encode at the atomic swap (TryAdd/TryUpdate CAS loop; a concurrent same-key registration can never be silently orphaned); the three active-encode flag registries carry per-encode generation tokens (the set is an unconditional indexer replace, so the newest generation always owns the flag; the old unchecked TryAdd was state-wise a no-op and is gone rather than checked); every flag clear (both startup catches, KillEncodeAndClearFlag, the monitor's finally) is compare-and-remove on the token, so a displaced generation's late exit can no longer drop a newer encode's flag (which had turned the near-ahead hold off mid-encode and let cache validation delete the live directory). Three red-green pins; the JF-647 pin unchanged. Gates: /simplify + code-review in-worker (the pin timing-margin fix applied; follow-ups filed same-turn and RENUMBERED to JF-668/JF-669 at merge time, the parallel JF-666 worker owning those numbers in main) plus the orchestrator's completion-gate marker pass (four angles, clean on all); suites 4730/4730 both TFMs (orchestrator-verified on the final state).
<!-- SECTION:FINAL_SUMMARY:END -->
