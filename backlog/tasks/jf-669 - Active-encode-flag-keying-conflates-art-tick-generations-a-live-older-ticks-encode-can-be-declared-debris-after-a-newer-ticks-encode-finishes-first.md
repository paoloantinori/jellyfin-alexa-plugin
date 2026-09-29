---
id: JF-669
title: >-
  Active-encode flag keying conflates art-tick generations: a live older-ticks
  encode can be declared debris after a newer-ticks encode finishes first
status: Done
assignee: []
created_date: '2026-09-29'
labels:
  - encode-gate
  - race
  - cache
dependencies: []
references:
  - >-
    backlog/tasks/jf-665 - Registration-side-of-the-same-key-speed-encode-re-register-race-orphaned-displaced-ffmpeg-at-the-registry-overwrite-unchecked-TryAdd-key-only-encode-flag-clear.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-29 same-turn from the JF-665 code-review round (high effort, finding 1 of 5). INHERITED exposure, not a JF-665 regression: JF-665's generation tokens made the clears strictly safer in this scenario (before, BOTH generations' key-only clears dropped the flag; after, only the newest generation's clear can), but the newest-generation-owns-the-flag contract still has no answer for CROSS-TICKS displacement.

THE EXPOSURE: the three active-encode flag registries are keyed by the bare cache key (episode/song itemId, audiobook parentId, variant cache key), while the per-key LOCKS and the CACHE DIRECTORIES are keyed by (key, artModifiedTicks). When an item's art changes mid-encode:

1. Episode X encodes under ticks=A (flag token T_A set, monitor running, dir {key}_A live).
2. Art changes; a second request computes ticks=B, takes lock(key, B) (a DIFFERENT lock), and MarkEncodeActive displaces T_A with T_B under the SAME flag entry.
3. The gen-B encode finishes FIRST; its finally compare-and-removes T_B, so the flag drops while the gen-A encode is still live.
4. A request that read ticks=A before the art change then sees ContainsKey(key)==false plus a no-ENDLIST playlist in dir_A, and ValidateEpisodeCacheAsync returns the interrupted-debris verdict: it Cleans up dir_A mid-encode and starts a duplicate encode, wasting the live encode's output.

Same shape on the song path (MarkEncodeActive after the prewrite) and the audiobook path (keyed by parentId).

REACHABILITY: needs an art change mid-encode plus a tick-stale request; rare, but the deletion of a live encode's directory is the same damage class as the JF-499 W3 vanish the fast paths already guard.

FIX DIRECTION (not done; needs its own design pass): make flag liveness art-tick aware, either by keying the flag entry by (key, artTicks) with presence readers falling back across tick generations, or by refcounting generations under one entry so the flag drops only when the LAST live generation exits. Every presence reader (ValidateEpisodeCacheAsync, the near-ahead hold, the prewrite serve gates, the concurrent-encode guards) reads by bare key today, so this is a semantics change across serve paths, deliberately not folded into JF-665's minimal fix.

VERIFICATION: a test that starts a ticks=A encode (fake ffmpeg), marks a ticks=B generation, lets the B monitor clear, and asserts the A directory is not treated as debris while A still runs.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
<!-- DoD items 4-8 are N/A: no session attributes, no HttpClient changes, no
interaction model/locale/NLU/E2E surface touched (controller-internal
concurrency fix, no user-facing strings changed). -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DESIGN PASS (written before code, 2026-09-29, worktree agent-a8f6369a6cec22309):

**CHOSEN: direction (b)** - generation refcounting under ONE bare-key entry. Precisely: the registry value becomes a per-key holder of art-tick slots (a gate + a map artModifiedTicks -> generation token); MarkActive registers a FRESH token in the CALLER's ticks slot (a same-(key,ticks) registration REPLACES the slot: the JF-665 newest-owns contract preserved WITHIN one generation); a handle's Clear compare-and-removes ITS (ticks, token) pair and drops the whole entry only when the LAST live slot emptied.

**Presence-reader roster (every reader reads the bare key today; under (b) NONE of them changes)**, with what each reader's honest question is and the (b) semantics for it:
1. `ValidateEpisodeCacheAsync` (the interrupted-debris verdict, line ~1055): question "does ANY live writer for this key exist?" before declaring a no-ENDLIST playlist interrupted debris. Under (b) the answer becomes exactly that, so the JF-669 sequence (newest-ticks generation's correct clear dropping the flag while an older-ticks generation still writes) is structurally impossible. Under (a) this reader needs artTicks threaded into its signature (4 call sites) plus an exact-vs-any policy decision, and the only safe policy is exact-OR-any = the union semantics (b) gives natively.
2. Episode fast-path + in-lock prewrite gates (2 sites) and song fast-path + in-lock prewrite gates (2 sites): serve the pre-written listing from the CALLER's (key, callerTicks) directory when an encode of the key is live. The tick-stale request of the exposure computes ticks=A while gen A is live: (b) keeps the flag up and serves dir_A's listing (gen A wrote it) - correct. Under (a) exact matching needs ticks threaded per gate, and the fallback question (dead listing of a third ticks generation served under an unrelated live flag) forces any-generation fallback anyway.
3. Near-ahead hold (`TryHoldForNearAheadSegmentAsync`): keys by BARE itemId by construction (`FindHlsDirectory` resolves the dir by `{guid}_*` prefix across ticks); wants any-generation liveness. (b) native; (a) needs a prefix scan.
4. Audiobook concurrent-encode guard (`TryGetValue(parentId)`): today bare-key. Under (a) exact matching would newly ALLOW a second concurrent audiobook encode under different ticks (two ffmpegs + two gate slots for the same book: a behavior change beyond this fix's scope); any-generation fallback = today's behavior = (b).
5. Test seams (`SetEncodeActiveForTest` / `EncodeActiveForTest`): the JF-665/668 pins must stay green UNCHANGED. Under (b) both seam signatures and the presence meaning are untouched; the seam's simulated generation lands in the ticks-0 slot (the no-art sentinel every flag fixture's real generation also uses: those items carry no images), so the JF-665 pin's same-slot displacement semantics survive verbatim. Under (a) the seams need redefining and the pins' meaning shifts.

**Failure modes weighed:**
- (a): per-reader exact-vs-any policy across 6 read sites (each a chance to pick wrong); prefix-scan matching on composite string keys (ambiguity risk); `ValidateEpisodeCacheAsync` signature churn; the seam stops emulating a real same-directory second generation; the widest possible diff across serve paths - exactly the "semantics change across serve paths" this task's own description warned about when deferring it from JF-665. Its ONLY precision gain over (b): debris sitting in a dir of ticks C gets cleaned while an unrelated ticks generation of the same key is live; today's bare-key behavior protects it too, so (a) fixes no live bug there.
- (b): the register-vs-empty-remove race - a registration landing in a holder whose registry entry a concurrent Clear just emptied and removed (or a Clear removing an entry a concurrent Register refilled) - flag lost while live, the JF-669 exposure reborn as a microsecond race. CLOSED STRUCTURALLY: every holder mutation AND the empty-check + outer compare-and-remove run under the holder's private gate, and MarkActive verifies INSIDE the gate that its holder is still the stored one (retry loop on mismatch; terminating because a mismatch implies a concurrent Clear made progress). Gate hold time is a few dictionary ops, no I/O, no awaits; the per-item semaphore locks (`LockItemAsync`) are untouched, so no lock is held longer than today.
- (b) residual coarseness, ACCEPTED: debris in a dir of ticks C can stay protected while an unrelated ticks generation of the same key is live (identical to today's bare-key behavior: no regression; reachability is art-changed-twice-plus-stale-debris; self-heals when the live generation exits and the next request validates).
- Third options rejected: keying the flag by the cache-directory string `{key}_{ticks}` IS (a) with a string composite (same reader churn plus string ambiguity - strictly worse); consulting a live-PROCESS registry from the debris verdict forks a second flag mechanism and violates the JF-668 build-on-ActiveEncodeHandle constraint (no process registry exists for the episode/song/audiobook paths).

**INVARIANT ESTABLISHED:** an active-encode flag entry is PRESENT iff at least one encode generation of that cache key is live; a generation is live from its MarkActive until its own handle's Clear, or until a NEWER same-(key, artTicks) registration displaces it (JF-665, preserved within one generation). Therefore no clear of one ticks generation - the newest generation's included - can drop the flag while another ticks generation of the same key still writes, and no presence reader (the ValidateEpisodeCacheAsync debris verdict first among them) can declare a live encode's directory debris.

**SHAPE (smallest change that establishes the invariant, all inside VideoAudioController.cs + the test file):**
- The three registries: `ConcurrentDictionary<string, object>` -> `ConcurrentDictionary<string, ActiveEncodeGenerations>` (docs updated).
- New private sealed class `ActiveEncodeGenerations`: the per-key holder (gate + slots + Register/RemoveGeneration/IsEmpty/Count).
- `ActiveEncodeHandle` keeps its JF-668 shape, gains the holder + artTicks fields; `MarkActive(registry, key, artTicks)` with the gate+verify loop; `PreMarkSentinel(registry, key, artTicks)` over a DETACHED holder (never creates registry presence; its Clear no-ops by construction); `Clear()`: gate -> compare-and-remove (ticks, token) -> drop entry iff empty. `KillAndClear` unchanged.
- `MarkEncodeActive` gains the artTicks parameter (song mark + song pre-mark sentinel, episode remux, variant core via spec.ArtModifiedTicks, audiobook, test seam with ticks 0).
- NO presence reader changes (ContainsKey/TryGetValue on the bare key keep compiling and now mean "any live generation").
- New internal seam `EncodeGenerationCountForTest` (deterministic pin wait for "the B generation's clear landed" without conflating it with A's liveness).

**PIN (red-green):** `StreamHlsEpisode_NewerArtTickGenerationClearsFirst_OlderTicksLiveDirNotDebris` - episode path, mirroring the JF-665 hung-encode pin + the JF-498 debris-test construction: item with primary art dated A; call 1 starts a hung fake ffmpeg (dir `{id}_A`, no-ENDLIST playlist, an original-marker file, stop-file loop); art mutated to B; call 2 starts gen B under ticks B (fake B parks on a stop file so both generations are structurally live together, then exits 0 with ENDLIST); wait for B's monitor completion log (unique in the window: A's monitor is parked) + settle so B's finally-clear lands; call 3 with art reverted to A (the tick-stale request) must NOT fire the debris verdict: dir_A's original marker intact (the debris-cleanup-fired discriminator, asserted FIRST so the red run fails on it) and no "Episode HLS cache invalidated" log line. RED PROOF: with the fix disabled (MarkActive temporarily pinning every generation into one shared ticks slot = the pre-fix conflation) the pin fails on exactly those debris assertions. The simultaneous-liveness premise (generation count 2 while both park) is observed with a non-fatal wait so the red run reaches the debris assertion instead of failing at the premise.

DONE 2026-09-29 (worktree agent-a8f6369a6cec22309, not pushed). Design (b) implemented exactly as above: the three registries are now `ConcurrentDictionary<string, ActiveEncodeGenerations>`; the holder owns the gate + slots AND its own registry location (a simplify-gate refinement: RegisterIfStored/ClearGeneration take only (artTicks, token), the handle dropped from 5 fields to 3, mispairing still unrepresentable because the holder+registry+key pairing is established at factory construction); MarkActive = GetOrAdd + RegisterIfStored (gate + stored-holder verify + retry); Clear = ClearGeneration (gate + compare-and-remove + drop-entry-iff-empty); PreMarkSentinel builds a DETACHED holder so it never creates presence and its clear is a structural no-op. NO presence reader changed: all ContainsKey/TryGetValue sites keep their bare-key reads and now mean "any live art-tick generation of the key". Test seams: SetEncodeActiveForTest/EncodeActiveForTest signatures and semantics untouched (the seam's generation lands in the ticks-0 slot, displacing same-ticks exactly like the JF-665 pins' fixtures, which encode imageless items whose real ticks are also 0); new EncodeGenerationCountForTest seam; the three seams share one EncodeRegistryFor selector.

RED-GREEN PROOF (documented runs): GREEN first - pin passed on both TFMs before any toggle. RED attempt 1 taught the honest lesson: a toggle that pinned ONLY the registration slot (RegisterIfStored called with 0L) left the handle's clear carrying the real ticks B, so gen-B's clear MISSED the slot and the flag never dropped - the pin PASSED, exposing the mistake (investigated, not shrugged). RED attempt 2 with the complete toggle (artModifiedTicks forced to 0 at the top of MarkActive = the pre-JF-669 conflation for both register and clear): the pin FAILED on both TFMs with exactly "the interrupted-debris verdict fired for a key whose older-ticks generation is still live (JF-669)" - the debris cleanup demonstrably fired. The same red run also exposed that the pin's first-cut marker-file assertion was VACUOUS (the debris path's re-encode re-runs the fake, which re-writes the marker): replaced with (a) the no-invalidation-log assertion (primary discriminator) and (b) gen-B's completed ENDLIST playlist surviving in dir_B (file-level evidence; Cleanup wipes every {id}_* directory and nothing recreates dir_B). Toggle reverted; pin green again on both TFMs.

GATES: /simplify (4 parallel angles on the final diff). Applied: the EncodeRegistryFor helper (reuse angle + altitude micro-note flagged the third inline copy of the registry-selector ternary); the holder-captures-registry+key shape (simplification F1: two pass-through handle fields and two per-call params deleted); the generation-rule doc dedup (simplification F2: MarkEncodeActive keeps the canonical account, MarkActive points at it and keeps only the retry-protocol account). Justified skips: EncodeActiveForTest reimplemented over the count seam (F3) - the presence seam must mirror production's ContainsKey read, not a derived count, and the transient mid-registration window makes them non-equivalent; a single global mutation lock replacing the per-holder gate (F4, also probed by the altitude angle) - it couples the three independent registries through one object, the opposite direction from this codebase's registry-race history. Efficiency angle: clean (all costs per-encode, not per-request; gate holds only dictionary ops). Altitude angle: no findings (verified the holder+slots owner, the holder/handle boundary, and the seam placement are all the right depth, and confirmed _activeAudioSpeedEncodeProcesses is a kill registry correctly left untouched).

code-review high (2 findings, both LOW severity, both APPLIED as doc-accuracy fixes on pre-existing behavior; nothing cut at the cap, no filing needed): F1 - the four prewrite-gate comments overstated "a completed cache must serve ffmpeg's ENDLIST playlist" as unconditional; under key-scoped (union) presence a completed foreign-ticks cache can still serve its surviving pre-written listing while an unrelated ticks generation runs (pre-existing bare-key behavior, the accepted residual coarseness) - all four comments now state the JF-669 key-scoped caveat. F2 - MarkEncodeActive's "present iff at least one slot is live" was false during the brief mid-registration window (entry stored, slot not yet written); the doc now states the window and that every reader errs conservative (skip debris verdict / serve pre-written / hold), and EncodeGenerationCountForTest's doc notes it can read 0 while EncodeActiveForTest reads true there. The review independently verified the mechanism sound: the stored-holder check closes the register-vs-remove race, no lock inversion, the detached-sentinel clear is a structural no-op, JF-665 same-ticks displacement preserved, all 6 mark sites and 9 presence readers accounted for, variant specs pass ticks consistent with their directory keying.

VERIFICATION (final tree, both TFMs, NEVER --no-build): build 0 warnings 0 errors (TreatWarningsAsErrors on); full suite 4755/4755 net9.0 + 4755/4755 net10.0, exit 0 (4754 baseline + the 1 new pin); the JF-665 pair, the JF-668 pin, and the JF-647 pins re-verified green UNCHANGED alongside the new pin after every review-driven edit. No interaction model, locale, NLU, or config surface touched (DoD items 4/6/7/8 N/A: no strings changed, controller-internal concurrency fix).
<!-- SECTION:NOTES:END -->

## Final Summary

Art-tick generation refcounting for the active-encode flags: each of the three flag registries now maps its bare cache key to a holder of live (key, artModifiedTicks) generation slots instead of a single newest-generation token. A same-ticks re-registration still displaces (the JF-665 newest-owns contract preserved within one generation), and the entry drops only when the LAST live generation's own compare-and-remove clear fires, so a newer-ticks encode finishing first can no longer orphan an older-ticks encode's liveness and let the interrupted-debris verdict delete its live directory. Presence readers are untouched (the flag now honestly means "any live art-tick generation of this key"). Pinned by StreamHlsEpisode_NewerArtTickGenerationClearsFirst_OlderTicksLiveDirNotDebris, red-proven by pinning all generations into one shared slot (the pre-fix conflation) and observing the debris verdict fire.

ORCHESTRATOR GATE-MARKER TAIL (2026-09-29, scrutiny items all passed: the retry loop's termination, the mid-registration window's conservative readers, and no same-ticks masking of cross-ticks regressions; 4 findings, all dispositioned): F1+F2 FILED same-turn as JF-675 (the widened prewrite-serve window for completed foreign-ticks caches - the task notes' "no regression" claim is exact only for flag liveness, corrected here - and the verdict's undocumented registry-family boundary); F3 APPLIED (the SetEncodeActiveForTest clear-arm hazard documented on the seam: it hard-removes bypassing the holder gate, safe only on fresh keys or post-monitor keys); F4 APPLIED (the song-path sentinel inline comment now states the structural JF-669 guarantee - the detached holder the registry never stores - instead of the superseded token-identity account, pointing at PreMarkSentinel's doc).
