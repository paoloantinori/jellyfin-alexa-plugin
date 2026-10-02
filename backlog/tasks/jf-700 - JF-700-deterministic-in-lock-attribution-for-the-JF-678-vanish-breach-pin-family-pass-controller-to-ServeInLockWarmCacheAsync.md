---
id: JF-700
title: >-
  JF-700 - deterministic in-lock attribution for the JF-678 vanish/breach pin
  family (pass controller to ServeInLockWarmCacheAsync)
status: To Do
assignee: []
created_date: '2026-10-02 01:42'
labels:
  - encode-gate
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-681 -
    JF-681-test-infrastructure-residuals-of-the-own-live-prewrite-pins-mid-registration-split-uncovered-attribution-seam-prewrite-read-count-park-assert-fault-observation.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the JF-681 simplify round (altitude/reuse finding, cut at the task's four-item scope cap; JF-681 landed the seam and moved the assert into the helper).

JF-681 gave the in-lock pins a deterministic in-lock-vs-fast-path attribution: the InLockWarmCacheProbeForTest seam fires at the ONE LockHlsItemAsync wrapper, and ServeInLockWarmCacheAsync (VideoAudioControllerTests) gained an optional `VideoAudioController? controller = null` parameter - when passed, the helper wires the seam and asserts the probe fired after the endpoint completes, so a >400ms pre-lock stall whose request then served from the FAST path fails the pin instead of passing under a false attribution. EIGHT pins opted in (the four JF-677 in-lock twins, the two JF-680 own-live prewrite pins, the two JF-681 mid-registration pins).

THE RESIDUAL: the JF-678 vanish/breach family of ServeInLockWarmCacheAsync callers (the InLockCacheVanishedAtServe twins and the breach pins: ~8 call sites at the former lines 7514/7553/7600/7712/7808/7869/7931/7981 on the pre-JF-681 file, find them by grep ServeInLockWarmCacheAsync minus the 8 opted-in pins plus the fault-observation pin) still attribute in-lock-vs-fast-path via the 400ms park assert ALONE - the exact probabilistic attribution JF-681 set out to close, now silently asymmetric across the same helper.

THE WORK (mechanical, test-only): pass `controller` as the 4th argument at every family call site whose endpoint enters a lock scope. CAVEATS: (a) the BREACH pins expect the endpoint task to THROW (FileNotFoundException propagation), and the helper's probe assert sits after `await endpointTask.WaitAsync(...)` inside the helper, so a throwing endpoint never reaches it - for those sites the deterministic attribution needs either a helper variant that asserts before the rethrow, or an explicit probe assert in the pin around the ThrowsAsync; decide per pin honestly, do not fake coverage. (b) one call site (the fault-observation pin) deliberately passes no controller - leave it. Red proof: removing the LockHlsItemAsync invoke flips exactly the pins that opted in (JF-681 verified this shape at 8/17).

VERIFICATION: full suite both TFMs (recipe: dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1, never --no-build); the 8 already-opted-in pins stay green; no production change.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (clean rebuild of the test project, both TFMs)
- [x] #2 dotnet test passes (4895/4895 both TFMs on the final state, recipe dotnet test -m:1, never --no-build after edits)
- [x] #3 No new compiler warnings introduced (clean-rebuild inventory: only the 2 pre-existing xUnit1030 pair on the untouched ConfigureAwait line of the throwing-probe pin)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: test-only change, no session attributes)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: test-only attribution hardening of existing pins, no new intent or handler logic)
- [x] #8 Locale response strings added to all 17 locales (N/A: no user-facing strings)
- [x] #9 /simplify passed (4 agents; findings applied, none blocking remaining)
- [x] #10 /code-review high passed (6 findings: 4 applied, 2 skipped with verified reasons, none out-of-scope so no JF-704 filing needed)
<!-- DOD:END -->

## Final Summary
<!-- SECTION:IMPL:BEGIN -->
Landed in the worker worktree (single commit, not pushed), baseline e572f76b, suite 4895/4895 both TFMs (test-only, no new pins: the baseline count is unchanged, the 8 family pins gained enforcement).

WHAT LANDED: the JF-678 vanish/breach family (all EIGHT remaining non-fault-observation ServeInLockWarmCacheAsync call sites) now passes `controller` to the helper, so its in-lock-vs-fast-path attribution is deterministic instead of resting on the 400ms park assert alone. No honest skips: every family site's endpoint enters a lock scope (all four in-lock scopes route through the LockHlsItemAsync wrapper), so all eight opted in.

THE FAULT-PATH MECHANISM (caveat (a) of the task): the four BREACH pins' endpoints FAULT by design (FileNotFoundException), so the helper's post-await assert was unreachable for them. Fix inside the ONE helper (no per-pin duplication): a `catch (Exception failure) when (endpointTask.IsFaulted)` arm asserts BEFORE the rethrow. Green: the probe fired, the assert passes, the endpoint's own exception rethrows for the pin's ThrowsAsync. Red: Assert.Fail carries InLockProbeNotFiredMessage PLUS the endpoint's real fault embedded type/message/stack (the JF-681 park-assert unwrap pattern, applied per code-review finding 1 so the assert never discards the cause the pin is built on); the ThrowsAsync then fails on the type mismatch with that text. The exception filter admits only endpoint faults, so a WaitAsync timeout or a canceled endpoint propagates untouched with its own diagnosis (the simplify efficiency finding). The assert is ONE local function (AssertProbeFired) with a fixed-at-entry toggle, called from the fault arm and once after the settle. The fault-observation pin still passes NO controller (caveat (b), untouched).

PER-SITE DECISIONS: 4 vanish twins (song/episode/audio-variant/audiobook InLockCacheVanishedAtServe_FallsThroughToReencode): green endpoints, plain 4th-arg opt-in, the pre-existing post-settle assert covers them. 4 breach twins (episode/song/audio-variant/audiobook InLockLiveGenerationVanishAtServe_FailsLoudNoReencode): faulting endpoints, the new fault-path assert arm. 0 sites on the "named comment + park assert" honest-skip list. InLockProbeNotFiredMessage's parenthetical generalized to "a fast-path serve won the race past the 400ms park window, or the endpoint faulted before reaching the lock" (truthful for the post-park pre-lock fault shape that reaches it; the pre-park shape is owned by ParkAssertFailureMessage).

RED PROOF (run three times: initial shape, post-simplify shape, final post-review shape; identical 17/18 on BOTH TFMs each time): commenting out the single LockHlsItemAsync invoke flips exactly: the 8 previously-attributed pins (4 JF-677 in-lock twins, 2 JF-680 own-live prewrite, 2 JF-681 mid-registration) on the post-settle assert; the 8 newly opted-in family pins (vanish x4 post-settle; breach x4 on the fault-path assert, e.g. "Expected FileNotFoundException, Actual FailException: the JF-681 in-lock probe never fired ... THE ENDPOINT FAULTED, KEPT FOR TRIAGE: System.IO.FileNotFoundException: Playlist of a live pinned encode vanished ..."); plus ONE flip outside the helper family: StreamHlsVideoAudio_ThrowingInLockProbe_ReleasesTheItemLock fails with "No exception was thrown" because it wires a THROWING observer directly at the seam (not a helper caller; a direct seam consumer, expected under the single-point disable and outside JF-681's "8/17" helper-caller count). The 18th, the fault-observation pin, stays green (no controller, by design). Restored: production file byte-identical (git diff empty), everything green.

GATES: Skill simplify (4 agents: reuse/simplification/efficiency/altitude). Applied: the settle-state local function replacing the duplicated assert blocks (unanimous finding), the IsFaulted filter (timeout/cancellation keep their diagnosis), the doc trims (mechanism lives once in the helper doc; the rot-prone "every call site now passes" roster sentence cut; the breach-group doc trimmed to pointer + red proof), the message parenthetical generalization. Skipped: none above threshold (the one-line per-twin "see the song twin" pointers are the file's existing twin-doc convention). Skill code-review high (6 findings): F1 embed-the-endpoint-fault APPLIED (red output above shows it working); F2 doc failure-signature wording APPLIED; F5 banned "word - word" hyphen break in authored prose APPLIED; F6 fixed-at-entry null-toggle APPLIED; F3 (timeout+concurrent-fault filter race) SKIPPED: the window is nanoseconds wide against a 20s budget and F1's embed already preserves the real fault in the message, while the proposed `ex is not TimeoutException` guard would silently skip the attribution assert for any endpoint-originated TimeoutException; F4 (the new message clause unreachable) SKIPPED as a false positive: the clause names the reachable POST-PARK pre-lock fault shape (the park assert passed at 400ms, the fault lands before lock entry), not the pre-park shape the reviewer targeted. NO real-but-out-of-scope findings: JF-704 not created. No production change (the temporary seam disables were reverted byte-identical). No deploy; do not push.
<!-- SECTION:IMPL:END -->
