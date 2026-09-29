---
id: JF-668
title: >-
  Encode-handle type and shared kill idiom for the HLS encode registries (JF-665
  follow-ups: make registry/key/token mispairing unrepresentable; one
  kill-a-maybe-live-process helper with a decided entireProcessTree policy)
status: In Progress
assignee: []
created_date: '2026-09-29'
labels:
  - playback-speed
  - encode-gate
  - refactoring
dependencies: []
references:
  - >-
    backlog/tasks/jf-665 - Registration-side-of-the-same-key-speed-encode-re-register-race-orphaned-displaced-ffmpeg-at-the-registry-overwrite-unchecked-TryAdd-key-only-encode-flag-clear.md
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DONE 2026-09-29 (worktree agent-ab273a9c819a38e47, not pushed). DoD 6/7/8 N/A (controller-internal; no model, locale, or handler surface).

PER-SUB-ITEM DISPOSITION:

1. ENCODE HANDLE TYPE: DONE as the private readonly struct `ActiveEncodeHandle` (registry + cache key + generation token, private fields, `Clear()`/`KillAndClear(Process)` as the only clear paths). `MarkEncodeActive` now returns the handle; the deleted statics `ClearEncodeFlag`/`KillEncodeAndClearFlag` became the handle's methods (docs moved with them); the parallel-parameter threading is gone from `WaitForFirstSegmentOrKillAsync` (cacheKey+registry+token params collapsed to one handle param), `StartHlsMonitor` and `MonitorFfmpegHlsAsync` (registry+token params collapsed the same way; the `var activeEncodes = activeEncodesTracker` alias deleted). All four mark sites (song, episode, variant core, audiobook) and every clear site migrated; the song path's pre-try sentinel became the named factory `ActiveEncodeHandle.PreMarkSentinel(registry, key)` (clear no-ops until the mark re-mints the handle; kill+dispose stay real). The JF-647/JF-665/JF-636 pins are UNTOUCHED and green on both TFMs (they drive the seams, not the signatures). One deviation from the ideal shape, forced by C# semantics and proven on this compiler: the handle's constructor must stay `public` because C# (unlike Java) does NOT let the enclosing class reach a nested type's private members (CS0122 on MarkEncodeActive's construction, build-verified); the type itself is private to VideoAudioController, so construction cannot happen anywhere else, and the ctor doc names the two intended mint shapes.

2. SHARED KILL IDIOM + TREE-KILL POLICY: DONE as `TryKillLiveProcess(process, reasonTemplate, params args)` (HasExited guard, Information log, `KillEncodeTree`, catch-race LogDebug) replacing the three drifted copies (KillSupersededSpeedEncodes, RegisterLiveSpeedEncode's displacement kill, MonitorFfmpegHlsAsync's stall kill). The POLICY lives in the one-line `KillEncodeTree(process)` (`entireProcessTree: true`, per the orchestrator's decision), which `ActiveEncodeHandle.KillAndClear` also calls, so the flag exists in exactly one place and every kill in the diff's surface is a tree kill (the /simplify and code-review rounds both flagged the two-shapes drift when KillAndClear kept a plain Kill; routing it through KillEncodeTree closed it with no logger coupling). The reason is a structured MESSAGE TEMPLATE + args (not a preformatted string): the /simplify efficiency and altitude angles flagged the eager interpolation and the lost named log fields, and the template shape restores each site's exact pre-JF-668 wording ({CacheKey}, {ItemId}, {DeviceId}, {Label}, {ParentId}, {StallBudget} stay destructured properties; the two helper logging calls carry a CA2254 pragma because the template arrives as a value). The stall site's Warning dropped its ", killing" tail (only "HLS encoding STALLED" is pin-asserted, verified) so a stall is one detection line + one uniform kill line instead of two kill announcements. A bonus consolidation from the altitude angle: `RemoveSpeedEncodeAndKill(entry, template, args)` owns the compare-and-remove + kill composite both registry kills share (the JF-647 never-delete-an-uninspected-registration rationale now has one home; the exit watcher keeps its remove-only call).

3. EARLIER DISPLACEMENT KILL: DONE as `KillDisplacedSpeedEncode(cacheKey)` (TryGetValue + RemoveSpeedEncodeAndKill on the observed pair, abandoned-by-construction rationale in its doc; the registration-time kill in RegisterLiveSpeedEncode stays as the documented BACKSTOP). Placement sharpened one step past the task text after the code-review round: the kill runs inside the per-key lock at the `SupersedeStaleEncodes` invocation, which was MOVED to right after the in-lock cache re-check miss, BEFORE `GetHlsDirectoryPath`/`Directory.CreateDirectory`/`DeleteHlsEncodeDebris` (the reviewer's finding: at the old position the prior writer stayed alive through the directory recreation AND the debris sweep, a residual by-path write window; killing before any directory mutation also means the sweep can never race a live writer). The JF-636 rationale for the hook position ("inside the lock, after the cache fast paths") is unchanged and the hook doc updated.

PIN (VideoAudioControllerTests, one new; all existing pins untouched): `StreamHlsAudioSpeed_SameKeyReEncode_KillsDisplacedEncodeBeforeNewFfmpegStarts` drives the REAL endpoint with a live prior encode planted on the `RegisterLiveSpeedEncode` seam under the exact same cache key; the fake ffmpeg's first action probes the prior's /proc state (shell-builtin read, no awk: a missing /proc entry or state Z counts as dead, so no tool absence can fake a dead read) and writes prior-state.txt BEFORE the first segment, so the file reads dead iff the kill landed within the probe's 0.5s SIGKILL-latency tolerance (the tolerance is documented in the pin; a kill that only follows the first-segment wait reads alive deterministically because the snapshot precedes the segment write that wait polls for). RED-GREEN, proven twice (once per script revision): with `SupersedeStaleEncodes` disabled the pin fails on both TFMs with "the displaced prior encode was still alive when the new ffmpeg started"; restored, it passes.

GATES: Skill /simplify (4 parallel angles on the diff; findings APPLIED: the template+args reason shape, KillEncodeTree shared by TryKillLiveProcess + KillAndClear, private fields + PreMarkSentinel named factory + SA1642-conformant ctor doc, RemoveSpeedEncodeAndKill composite; SKIPPED with reasons: the sentinel-as-`default` variant, rejected because a null-tolerant Clear taints the invariant-carrying type with nullability noise for a wash the agent itself noted; the SupersedeStaleEncodes hook rename, declined by the agent as churn; the mark-site registry choice staying doc-enforced, noted as a larger refactor outside this task's fence). Skill code-review high (4 findings, all dispositioned: F1 residual recreate-then-kill window FIXED by moving the hook before Directory.CreateDirectory; F2 private-ctor REJECTED with build evidence, the CS0122 above; F3 the pin's tolerance overclaim + awk-absent vacuous green FIXED by the builtin-read probe and the honest doc claim, the 0.5s tolerance itself retained because a strict single read trades a deterministic red proof for a scheduler-dependent false-red under CI load; F4 the stall double-log FIXED by dropping the Warning's kill tail). Nothing cut at the cap; no new backlog tasks filed.

VERIFICATION: Release-equivalent Debug builds 0 warnings 0 errors both TFMs on the final tree (TreatWarningsAsErrors on); full suite 4742/4742 net9.0 + 4742/4742 net10.0, exit 0 (4741 baseline at main tip + the 1 new pin), both red proofs observed on both TFMs before the final run.
<!-- SECTION:NOTES:END -->

ORCHESTRATOR GATE-MARKER TAIL (2026-09-29, 5 findings, all dispositioned): F2 APPLIED - the ctor is now PRIVATE with a public static MarkActive factory on the struct (the worker's CS0122 rejection only ruled out the bare private ctor; the factory shape compiles, PreMarkSentinel proved it), so mispairing is now unrepresentable, not doc-advised; F4 APPLIED - the hook comment no longer overclaims "before ANY directory mutation" (the stub cleanup above the hook also touches directories; the honest invariant cites the missing/empty-playlist guards plus the JF-428 pin protocol, neither pinned, with a do-not-hoist warning); F5 APPLIED - the pin's probe tolerance widened 0.5s -> 2s (40x0.05s) with the trade documented (deterministic red kept, CI false-red window shrunk); F3 DOCUMENTED at TryKillLiveProcess (probed on MEL 9.0.11: through the params wrapper the analyzer checks placeholders in one direction only; template edits need an eyed re-read); F1 NOTED as this task's own documented non-goal (the episode-audio/song/remux/audiobook paths keep the two-writers hazard; JF-665's scope note owns that boundary). Encode-family pins 16/16 both TFMs after the fixes; full suite 4742/4742 both TFMs.
