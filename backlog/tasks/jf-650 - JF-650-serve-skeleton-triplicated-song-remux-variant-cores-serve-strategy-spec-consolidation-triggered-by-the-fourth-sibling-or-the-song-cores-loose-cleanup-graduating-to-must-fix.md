---
id: JF-650
title: >-
  JF-650 - serve skeleton triplicated (song/remux/variant cores):
  serve-strategy-spec consolidation, triggered by the fourth sibling or the song
  core's loose cleanup graduating to must-fix
status: Done
assignee: []
created_date: '2026-09-27 08:20'
updated_date: '2026-10-04'
labels:
  - tech-debt
  - refactor
  - streaming
dependencies: []
references:
  - >-
    backlog/tasks/jf-637 -
    JF-636-follow-ups-consolidate-the-variant-HLS-machinery-the-JF-632-gate-preamble-and-the-slot-resolution-walk.md
  - JF-537.1
  - JF-676
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-637 altitude review (consolidation landed with the two tight siblings absorbed; the loose siblings are verified genuinely different, so the residual triplication is now a NAMED follow-up, not an undocumented gap).

THE RESIDUAL: the ~80-line serve skeleton (cache-hit serve, encode-in-progress concurrent serve, gate, start, first-segment wait, monitor, post-encode serve) now exists three times: the song core, the remux core, and the new shared variant core (ServeVariantHlsAsync). The altitude review verified the absorption blockers are REAL: (a) flag-vs-prewrite ordering differs per path and is load-bearing (song: flag AFTER prewrite so the flag implies listing-on-disk; remux: flag BEFORE process start with the prewritten-playlist fallback covering the race; variant: no prewrite); (b) registries and serve calls differ (_activeVideoAudioEncodes + overrideToken for song; ServeEpisodePlaylist(startTicks) for remux; _activeEpisodeEncodes + ServePlaylistWithToken for variant); (c) the song core has NO debris-cleanup step and no JF-499 vanish-guard on its fast path (it is looser than the family doc implies); (d) remux carries its own transcode tier, JF-537 cap warning, and extended monitor args.

THE HONEST DEEPER SHAPE (named by the review): a serve-strategy spec (TryServeWhileEncoding, prewrite-after-start, ServeAfterEncode delegates + registry + token-override members), absorbing the three skeletons with the ordering differences expressed as strategy hooks instead of delegates-per-site. Natural trigger: the next variant-HLS endpoint (fourth sibling), or whenever the song core's missing debris-cleanup/vanish-guard (item c) graduates from accepted-loose to must-fix (it is a real gap: the song path can leave debris and cannot recover a vanished cache dir the way the variant path can).

SCOPE GUARD: do NOT attempt this before JF-537.1 (cache-rooted transient mode) lands or is declined; both touch the same serve paths and the strategy refactor should absorb the final shape, not an intermediate one.

AUDIT UPDATE (2026-10-02): trigger item (c) is RESOLVED - JF-676 (commit 33401491) landed the ticks-scoped debris verdict + vanish re-probe on the song core and every HLS path, so the "song core's loose cleanup graduates to must-fix" trigger can no longer fire. The only remaining trigger is the fourth serve sibling; the flag-vs-prewrite ordering and registry/token blocker notes stand. Scope guard unchanged: not before JF-537.1 lands or is declined.

DECLINED (2026-10-04): trigger verified UNMET at the verification baseline 628bb56a (main's tip at check time); no code ships. Main has since advanced past it (fa2f4e86, the JF-674 closure) touching zero Controller/ files, so the verdict stands. Both trigger halves checked against the code, not the notes:

1. FOURTH SIBLING: does not exist. The serve-skeleton count is still three: song (`StreamHlsVideoAudioCore`), remux (`StreamHlsEpisodeCore`), variant (`ServeVariantHlsAsync`), all in VideoAudioController.cs (line anchors in the Notes). `ServeVariantHlsAsync` has exactly two callers (`StreamHlsEpisodeAudioCore`, `StreamHlsAudioSpeedCore`), and both predate this filing: audio-speed landed 2026-09-26 (JF-636, b90ae051) and the filing was written 2026-09-27 from the JF-637 altitude review that absorbed those two into the variant core. The 32 commits touching the controller since the filing (rev-list count 33a5cf52..HEAD; predominantly JF-647 through JF-731) are hardening/pin/race work on the existing three cores: no new skeleton, no new HLS endpoint, no new controller file (`git log --diff-filter=A` on Controller/ empty; the production-tree `-S StreamHls` sweep, Jellyfin.Plugin.AlexaSkill/ excluding the controller, returns 0 commits). JF-637's parallel "third variant" trigger (its hypothetical VariantHlsKind wording-table deepening; the mechanism today is `VariantHlsSpec`, constructed at only two sites) is likewise still at two variants.

2. LOOSE CLEANUP GRADUATING TO MUST-FIX: extinguished, not merely optional. Item (c) named the song core's missing debris-cleanup step + missing JF-499 vanish-guard on its fast path; both halves are landed and code-verified: the fast path AND the in-lock double-check now run the ticks-scoped debris verdict through `TryServeValidatedHlsCacheAsync`/`ValidateVideoAudioCacheAsync` (JF-676; the validator's no-ENDLIST-and-not-live row calls `CleanupHlsGeneration`), with the vanish re-probe on `ResolveServeContentAsync` (JF-677) and the follow-ons JF-678 (vanish-at-serve family + `GuardInLockVanishFallThrough`), JF-679, JF-686. A resolved gap cannot graduate from accepted-loose to must-fix.

3. SCOPE GUARD: also unresolved. JF-537.1 (cache-rooted transient mode) is still To Do, neither landed nor declined, so the filing's own guard would block the attempt even if a trigger had fired.

THE CURRENT TRIPLICATION'S COST, named honestly (what a fourth sibling would duplicate):

- What each of the three cores still carries: the ~80-line orchestration ORDER: cache-hit validated serve, in-lock concurrent validated serve + vanish guard, stub cleanup + directory recreation, mark-before-start, gated ffmpeg start with clear-on-throw, first-segment wait with kill-on-failure, directory registration, monitor handoff, post-encode serve.
- What a fourth sibling would NOT duplicate (the cost is materially lower than at filing): JF-637/668/676/677/678 have since moved every historically bug-prone invariant into shared helpers a new sibling would simply call: `WaitForFirstSegmentOrKillAsync`, `MarkEncodeActive`/`ActiveEncodeHandle` + the shared kill idiom, `ValidateHlsCacheAsync` (the debris verdict), `TryServeValidatedHlsCacheAsync` (the verdict+serve composite, now covering the fast paths, the in-lock rows, AND the audiobook rows), `GuardInLockVanishFallThrough`, `StartFfmpegProcessGatedAsync` (the JF-428 pin protocol). A fourth sibling today copies the ordering and contributes its real differences; the invariants land once.
- The genuine blockers a serve-strategy spec would still have to express as hooks (re-verified in code, unchanged from the filing): (a) flag-vs-prewrite ordering: song prewrites AFTER the gated start (the JF-428 pin rule, comment at the `WriteVideoAudioPlaylist` site), remux carries its own prewrite fallback shape, variant deliberately has NO prewrite (the JF-536 scope-(c) decision documented on `ServeVariantHlsAsync`); (b) registry + serve-call divergence: `_activeVideoAudioEncodes` + overrideToken + startTicks slice (song) vs `ServeEpisodePlaylistAsync(startTicks)` (remux) vs `_activeEpisodeEncodes` + `ServePlaylistWithTokenAsync` (variant); (c) remux's own transcode tier, JF-537 cap warning, extended monitor args; (d) estimate-function divergence (`EstimateEncodeBytes`/`EstimateArtEncodeBytes` vs `EstimateEpisodeAudioEncodeBytes` rate-scaled by `EstimateScalePerMille`).
- The residual risk being accepted: an invariant that belongs in the ORDER (not the steps) must still be fixed three times; the 2026-09-27..10-04 hardening wave (JF-665/668/669/675/676/677/678, each touching all paths) is the lived example. That cost is bounded by the shared-helper layer above and stays accepted.

RE-ARMING: the filing's second original trigger half is extinguished (item c, above), so the consolidation fires only when a FOURTH serve skeleton actually materializes (a new variant-HLS endpoint that cannot ride `ServeVariantHlsAsync`), or when JF-537.1 resolves AND an orchestration-order change must hit all three cores at once (the conjunction is this decline's own bar that the consolidation must also pay for itself; the filing's scope guard alone lifts the moment JF-537.1 lands or is declined, and that by itself does not re-arm the task). Until then this stays declined.
<!-- SECTION:DESCRIPTION:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
TRIGGER VERDICT FIRST: DECLINED, trigger verified UNMET at the verification baseline 628bb56a (main's tip at check time; main has since advanced via the JF-674 closure, no Controller/ file touched); no code ships, the delta is this task file only. Both trigger halves were checked against the code, not the notes: no fourth serve skeleton exists (still three cores, the variant core's two callers both predating the filing, and the 32 controller commits since are hardening on the existing three), and the loose-cleanup trigger is extinguished (JF-676/Done landed the debris verdict + vanish re-probe on the song core, code-verified); the scope guard would ALSO have blocked the attempt (JF-537.1 still To Do). The full verdict, the honestly-named triplication cost (what a fourth sibling would and would NOT duplicate, the four strategy-hook blockers, the accepted residual), and the re-arming conditions are in the Description's DECLINED section; the verification commands and line anchors are in the Notes. DoD: Release -warnaserror 0 errors 0 warnings on the final state; #2/#3 satisfied by the doc-only delta on a tree byte-identical to main's 5143/5143-verified state (no suite run required, no code shipped); #4-#8 N/A (no code, model, locale, or handler surface touched); #9/#10 the two gates on the doc diff, outcomes in the Notes. WORKTREE: agent-a0c93ac2e6d9edba1, baseline-aligned to the JF-703 merge before the verdict; no merge into main by the worker.
<!-- SECTION:FINAL_SUMMARY:END -->

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

## Notes

<!-- SECTION:NOTES:BEGIN -->
DECLINE VERIFICATION EVIDENCE (2026-10-04, all against merge 628bb56a):

- Sibling count: method inventory of VideoAudioController.cs (grep of the method declarations) shows the three cores and no fourth (line anchors: `StreamHlsVideoAudioCore` :453, `StreamHlsEpisodeCore` :844, `ServeVariantHlsAsync` :2100); `ServeVariantHlsAsync(` has exactly two call sites (:1690 StreamHlsEpisodeAudioCore, :1880 StreamHlsAudioSpeedCore; `VariantHlsSpec` constructed at :1692/:1882); `git log -S "StreamHlsAudioSpeedCore"` dates audio-speed to b90ae051 2026-09-26 (pre-filing); `git log --diff-filter=A` on Controller/ since 2026-09-27 is empty; the production-tree sweep `git log --oneline --since=2026-09-27 -S "StreamHls" -- Jellyfin.Plugin.AlexaSkill/ ':(exclude)Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs'` returns 0 commits (scoped to production code; a repo-wide pickaxe also hits test/backlog files and is not the claim).
- Loose cleanup: the song core's fast path (TryServeValidatedHlsCacheAsync call at :493) and in-lock double-check (call at :543) both pair ValidateVideoAudioCacheAsync with the serve; ValidateHlsCacheAsync's no-ENDLIST-and-own-generation-dead row calls CleanupHlsGeneration (the debris clean); ResolveServeContentAsync owns the vanish re-probe (JF-677); GuardInLockVanishFallThrough covers the in-lock vanish fall-through (JF-678). JF-676 task status: Done.
- Scope guard: JF-537.1 frontmatter status To Do (checked 2026-10-04).
- Doc-only proof: `git diff 628bb56a --stat` (the verification baseline; do NOT diff against a moving main) on the final state shows only this task file; the code tree is byte-identical to that baseline, which the JF-703 merge verified at 5143/5143 both TFMs. Main has since advanced (fa2f4e86) with zero Controller/ files in its delta.
- Build: `env -u NUGET_PACKAGES -u NUGET_HTTP_CACHE_PATH dotnet build Jellyfin.Plugin.AlexaSkill.sln --configuration Release --no-restore -warnaserror` on the final state: 0 errors, 0 warnings (restored first; the worktree was fresh).

GATE /simplify (4 parallel angles on the doc diff; all four returned). The altitude angle independently re-verified every shared-helper claim per-core (all six helpers exist AND are called by all three cores; StartFfmpegProcessGatedAsync sharing even extends to the audiobook + non-HLS paths) and confirmed the re-arming conditions name observable mechanisms; the efficiency angle confirmed nothing executable changed. APPLIED (6, one family + two singles): the Final Summary compressed to verdict-first pointer form and the line anchors single-sourced into the Notes (simplification angle: the decline was stated three times inside one file, the enumerations now live once in the Description); the RE-ARMING block compressed and corrected (it re-spelled conditions labeled "unchanged" while one original half is now extinguished); the "VariantHlsKind enum" misnomer fixed (no such symbol exists; it is JF-637's name for a hypothetical deepening, today's mechanism is `VariantHlsSpec`, construction sites verified at :1692/:1882); the frontmatter `references` gained JF-537.1 and JF-676 in bare-ID form (the two load-bearing external states were prose-only); the worktree name single-sourced to the Final Summary. SKIPPED (1): the altitude angle's observation that JF-537.1's own file carries no back-pointer to this task (dependencies entry or description mention). Declined explicitly: the re-arm conditions self-surface to whoever pays the triplication (the reporter's own cost assessment is near-zero), a Done task carrying a dependencies entry is dead bookkeeping, and the machine-readable link now exists from this side's `references`. JUSTIFIED NON-FINDINGS: the reuse angle verified every external fact the DECLINED text restates carries a resolvable pointer (task ID, commit SHA, or code symbol+site) and rated the Description/Notes overlap within the house convention; the shifted (a)-(d) lettering vs the filing's list (item c extinguished and dropped, remux tier renumbered, estimate divergence added) is kept as the dated-addenda pattern with this note serving as the anchor.

GATE /code-review high (on the doc diff; the DECLINE VERDICT itself was independently confirmed sound: the reviewer re-verified the three-core inventory with exact line anchors, JF-676 Done at 33401491, JF-537.1 To Do, all six shared helpers called per-core including the audiobook rows, no fourth skeleton, and a net-zero route diff since the filing). 5 findings, ALL defects in the recorded evidence rather than the verdict, ALL APPLIED same-turn with every number re-verified by my own commands before rewriting: (1) the doc-only proof cited "current main (merge 628bb56a)" and `git diff main --stat`, false at commit time because main advanced to fa2f4e86 (the JF-674 closure, merged by another worker mid-session); re-anchored to the explicit baseline `git diff 628bb56a --stat` with the advance noted (its delta touches zero Controller/ files, verified); (2) "repo-wide -S StreamHls finds nothing outside VideoAudioController" was false as a command/conclusion pair (a repo-wide pickaxe hits 37 test/backlog commits); rescoped to the command actually run, the production-tree sweep excluding the controller, re-run and confirmed 0; (3) "the 30 commits" was a miscount through a name-only pipe; corrected to the rev-list-verified 32 (33a5cf52..HEAD); (4) the RE-ARMING parenthetical claimed the filing's scope guard is satisfied only at the full conjunction, but the guard text lifts on JF-537.1 resolving alone; rewritten to separate the guard (lifts on JF-537.1) from this decline's own pay-for-itself bar (the order-change conjunct); (5) DoD #10 was ticked with no /code-review outcome recorded; resolved by this paragraph (the gate could only be recorded after it returned).
<!-- SECTION:NOTES:END -->
