---
id: JF-637
title: >-
  JF-636 follow-ups: consolidate the variant-HLS machinery, the JF-632 gate
  preamble, and the slot-resolution walk
status: To Do
assignee: []
created_date: '2026-09-26 14:48'
labels:
  - tech-debt
  - refactor
  - consolidation
dependencies: []
references:
  - JF-636
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-636 (/simplify, three parallel reviewers) review round. The feature shipped correct; these are the deliberate NOT-NOW consolidations the reviewers flagged as repeat-offender shapes. All in Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs unless noted.

1. Variant-HLS core extraction (BLOCKING-class from the quality agent, consciously skipped): StreamHlsEpisodeAudioCore and StreamHlsAudioSpeedCore are near-identical ~120-line lock/fast-path/debris-cleanup/first-segment-wait/gate/monitor blocks (with StreamHlsVideoAudioCore and the audiobook core as older, looser siblings). Historically bug-prone invariants (JF-499 W3 race guard, JF-498 debris cleanup, CA2025 monitor boundary, double try/catch flag cleanup) had to be fixed copy by copy. Extract one shared ServeVariantHlsAsync(cacheKey, artModifiedTicks, validation, ffmpegArgs, estimateBytes, label, encodeFlags) parameterized over the ~4 real differences. Also: the `if (Guid.TryParse(...))` guard around token validation is always-true for the speed route (copied shape), and the speed path rides the "Episode"-named helpers/_activeEpisodeEncodes registry (rename or doc as variant-generic).

2. First-segment-wait helper: the 200x100ms poll + kill/dispose/TryRemove catch pair is now in its 4th inline copy (remux, song, episode-audio, audio-speed). WaitForFirstSegmentOrKillAsync(process, hlsDir, playlistPath, cacheKey, label) collapses them; do it together with item 1.

3. JF-632 gate preamble (SetPlaybackSpeedIntentHandler.cs:~124 vs SleepTimerIntentHandler.cs:~140): the ledger-snapshot + IsVideoAppMedium/ledgerVideoRouted gate is now copied verbatim twice. Extract one shared helper (BaseHandler or DeviceQueueManager) returning the gate evidence so the third transport intent cannot drift.

4. ER_SUCCESS_MATCH authority walk: third private copy (EpisodePosition.IsLatest, BrowseLibraryIntentHandler.GetCanonicalSlotValue, PlaybackSpeed.Resolve). A shared SlotResolution.FirstMatch(Slot) enumerator would own it; the per-slot-resolver convention is deliberate, so decide at the fourth copy.

5. DeviceQueue four-map lockstep (Alexa/Playback/DeviceQueue.cs): the base/rate pairing across Active/Pending maps is comment-enforced at 5 sites; a private paired-write helper would harden the "pending rate exists iff pending base exists" invariant (the RecordLaunchBase short-circuit checks only PendingLaunchBaseMs).

6. Torn launch-scope reads outside the event path: GetActiveLaunchScope(deviceId,itemId) exists since JF-636 and ComposeEventPositionTicks/ResolveResumedAudioLaunch use it; any future reader needing both base and rate must use it too (two individual reads can straddle a concurrent RecordLaunchBase).

Done as part of JF-636 instead (not to redo): the 0.75x JF-521 clamp inversion, superseded-encode killing (_activeAudioSpeedEncodeProcesses + KillSupersededSpeedEncodes), the audiobook honest refusal (CannotChangeSpeedForBook), and the AppendEventAudioHlsTail shared ffmpeg tail.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes (2026-09-27)

Behavior-preserving consolidation; #4-#8 are satisfied vacuously (no session attributes, HttpClient, interaction model, new handler surface, or locale strings were touched by this refactor; #1-#3/#9/#10 ran for real).

**Item 1 (DONE):** `ServeVariantHlsAsync(ValidatedRequest validation, VariantHlsSpec spec)` in `Controller/VideoAudioController.cs` now owns the shared variant-HLS machinery ONCE: the JF-499 W3 fast-path vanish guard (via `TryServeValidatedEpisodeCacheAsync`), per-key lock + stub cleanup + concurrent-generated serve, JF-498 per-file debris cleanup, the encode-flag bookkeeping with the double try/catch cleanup, the first-segment wait, and the CA2025 monitor boundary; the merged JF-536 scope-(c) no-prewrite rationale lives in its doc. `StreamHlsEpisodeAudioCore` and `StreamHlsAudioSpeedCore` are now thin spec builders contributing only the real differences (cache key, art ticks, ffmpeg-args builder with each variant's exact context log, lazy `Func<long>` estimate, labels, log delegates preserving the exact per-variant strings, and the speed-only `SupersedeStaleEncodes`/`OnEncodeLive` hooks). The episode remux and song cores stay OFF this core deliberately (their prewrite machinery interleaves differently; documented in the shared core's doc). The Episode-named helpers (`_activeEpisodeEncodes`, `ValidateEpisodeCacheAsync`, `TryServeValidatedEpisodeCacheAsync`) are doc-marked VARIANT-GENERIC, names kept. The instructed deletion of the `if (Guid.TryParse(...))` token-validation guard on the speed route was NOT done: the pinned test `StreamHlsAudioSpeed_InvalidItemId_Returns400` sends "not-a-guid" without a token and pins the 400 that the guard's else-path produces (the core's `ValidateVideoAudioRequest`), so the guard is live, not dead code; deleting it would flip that pinned outcome to 401.

**Item 2 (DONE):** `WaitForFirstSegmentOrKillAsync(process, hlsDir, playlistPath, firstSegmentFileName, cacheKey, activeEncodes, logFailure, errorBody)` in the same file collapses the four inline 200x100ms poll loops (song, episode remux, episode-audio, audio-speed). It owns poll + failure cleanup (`KillEncodeAndClearFlag`, also used by all three cores' catches); each caller keeps its exact warning string, registry, and 500 body. The audiobook/album path's fifth wait is deliberately excluded (its monitor already owns disposal; documented in the helper doc so a future pass does not fold it in). Signature differs from the task sketch (adds the segment file name, registry, log delegate, and error body) because those genuinely differ per site and `SafeExitCode` must be read before the kill.

**Item 3 (DONE):** the landscape shifted after filing: JF-635 already extracted the gate as `PlaybackLaunchBuilder.ResolveScreenOwningMedium` and migrated SleepTimer + the loop family. The remaining work was migrating SetPlaybackSpeed's inline copy (classifier + raw ledger-route belt) onto that resolver, which this change does; the resolver's consumer list is updated. Gate decisions are identical for every reachable ledger shape (all VideoApp-route recorders store video-kind/channel/book/Audio ids, which the kind kernel classifies VideoApp-family; confirmed independently by the code-review pass).

**Item 4 (DEFERRED, per the decide-at-the-fourth-copy rule):** the three ER_SUCCESS_MATCH walks are NOT identical in walk semantics: `EpisodePosition.IsLatest` returns a decision from the first authority carrying a non-null resolved value (terminating even to answer false), `PlaybackSpeed.Resolve` continues past authorities whose id does not resolve, and `GetCanonicalSlotValue` continues until a name appears. Only the 3-line guard shell is shared; the per-slot-resolver convention stays.

**Item 5 (DONE):** `DeviceQueueManager` gains the paired-write family `WritePendingLaunchScope` / `WriteActiveLaunchScope` / `RetirePendingLaunchScope` owning the "rate entry exists iff base entry exists" invariant (and the reason it is load-bearing: the active short-circuit reads only the base map). All three write sites use them; the trim and reset-survival docs now honestly record their residual convention-enforced status (per-map eviction order can in principle split a legacy mixed-age pair near the cap; the structural fix, a single scope-per-key map, is the named follow-up).

**Item 6 (VERIFIED, no change needed):** `GetActiveLaunchScope(deviceId, itemId)` has existed since JF-636, its doc owns the torn-read constraint, and both readers needing base+rate together (`ProgressReporter.ComposeEventPositionTicks`, `PlaybackLaunchBuilder.ResolveResumedAudioLaunch`) already use it; single-value readers route through `GetActivePlaybackRate`, which reads through the same scope snapshot.

**Bugs noticed, NOT fixed (pre-existing, filed with the orchestrator):** (a) the speed-encode exit watcher's unconditional `TryRemove` can delete a re-registered same-key encode's registry entry within its 1s poll window, degrading a later supersede kill to the conservative no-kill (JF-636 code, moved verbatim); (b) `TrimLaunchBaseIfNeeded`'s independent per-map eviction can orphan a rate half near the cap on legacy mixed-age files (the structural fix is the scope-per-key map); (c) the file carries one pre-existing unbalanced CA3003 pragma (disable at the audiobook concurrent-serve guard, restore count one short), silently widening that suppression zone to the next restore.

**Test summary (identical to baseline before any change):**
`Passed!  - Failed:     0, Passed:  4468, Skipped:     0, Total:  4468, Duration: ... - Jellyfin.Plugin.AlexaSkill.Tests.dll` on BOTH net9.0 and net10.0. Build: 0 errors, 0 new warnings (the 2 xUnit1013 warnings on the untouched `SetPlaybackSpeedIntentHandlerTests.Dispose` predate this change). Gates: /simplify (4 parallel reviewers; findings applied: LogCachedHit log-only delegate, lazy encode-only inputs incl. `Func<long>` estimate, `KillEncodeAndClearFlag`, `RetirePendingLaunchScope` split, call-site comment dedup) and /code-review high (findings applied: doc accuracy on the pairing invariant, explicit owner-device param on `RegisterLiveSpeedEncode`, audiobook-exclusion and ceiling-calibration docs; 2 pre-existing items filed, see above).

<!-- SECTION:NOTES:ORCHESTRATOR-SIMPLIFY:BEGIN -->
Orchestrator /simplify round (2026-09-27, four fresh reviewers on the final state; the worker's internal round was verified, not trusted):
- APPLIED: EstimateBytes Func -> EstimateScalePerMille plain number (the estimate is pure arithmetic over data the core holds; the laziness contract was copied from BuildFfmpegArgs where it IS load-bearing, here it protected nothing; also removes the identical base expression both builders carried). The ?d= hint read once into deviceIdHint (was read twice, with a 3-line comment justifying the second read). The two ceiling-calibration comment copies shrunk to pointers at the wait helper's doc.
- SKIPPED with reasons: S1 (the 5 wording members -> 2 with core-owned templates, byte-identical rendered text): preserves structured placeholders (StartTicks/RatePerMille) that carry triage value in structured logging, and the deeper form (VariantHlsKind enum + core-owned wording table) has an explicit trigger, the THIRD variant, recorded here and in JF-650; taking it now inverts the extension point with only two variants. deviceIdForLedger rename: name is stale but live (three reviewers confirmed the local is consumed); zero-behavior churn skipped.
- DEFERRAL-NOTE CORRECTION (altitude reviewer): item 4's shared shape, at the fourth ER walk, is a lazy IEnumerable<ResolutionValue> enumerator with each caller a FirstOrDefault(predicate) keeping its own semantics; a walk-mode enum parameter would be the WRONG depth.
- FILED same-turn: JF-650 (serve-strategy-spec consolidation for the triplicated skeleton), JF-651 (the ~9x Guid.TryParse+ValidateStreamToken route preamble extraction, from the altitude review's item-4 verification).
