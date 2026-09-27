---
id: JF-655
title: >-
  JF-655 - a speed request with nothing playing restarts stale audio at the new
  rate (the medium gate trusts the persistent ledger alone); require an
  active-playback signal before the re-launch paths
status: Done
assignee: []
created_date: '2026-09-27 13:31'
updated_date: '2026-09-27 17:07'
labels:
  - playback-speed
  - device-found
  - e2e-finding
dependencies: []
references:
  - backlog/tasks/jf-636*.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 from the full e2e battery (99 passed / 42 failed; the 40 open-error failures are the chronic class, the fast-mode flip re-verified green in isolation; THIS is the one deterministic real finding).

THE BEHAVIOR (reproduced isolated, it-IT, simulate-skill): 'a velocità uno e mezzo' with NOTHING actually playing answers 'Velocità uno e mezzo.' and re-launches the last-played track at the new rate, instead of the expected cold refusal 'Nessun contenuto in riproduzione'. Root cause: SetPlaybackSpeed's medium gate (ResolveScreenOwningMedium) reads the DEVICE QUEUE LEDGER (RecordLastPlayed), which persists long after playback stops. A stale Audio ledger entry makes the resolver say Audio -> the JF-636 speed re-launch path fires. The user's mental model is "change what is playing"; hours after music stopped, the request silently restarts old audio at a new speed (and on a REAL device, a one-shot speed request killing/restarting a stale stream is the JF-635 class of surprise).

FIX DIRECTION: the speed (and the loop-family and sleep-timer gates that share ResolveScreenOwningMedium) should require an ACTIVE playback signal, not just a ledger entry: corroborate with a live signal (a recently-active playstate: the plugin's own PlaybackStarted/Stopped tracking, InteractionDiagnostics.SincePlaybackStarted, or a /Sessions playstate check bounded to the device) before the re-launch/apply paths; a stale ledger alone yields the honest refusal (the JF-632/JF-564 refusal shape). Watch the JF-639 standing-rate threading and the F1 residual-seed path (TvNextUp) which also rely on the medium gate: the active-signal requirement must not break the genuinely-playing cases (pinned tests: SetPlaybackSpeed tests, Loop family, SleepTimer arming).

VERIFICATION BAR: the e2e row passes cold (nothing playing -> 'Nessun contenuto in riproduzione'); a playback-then-speed sequence still applies the rate (the JF-636 behavior unchanged when audio is genuinely active); the suite green; the e2e fast/full chains for it-IT re-run clean.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**State home (chosen):** the per-device active-audio flag lives ON `DeviceQueueManager`
(`_activeAudioPlaybackDevices`, a `ConcurrentDictionary<string, byte>` beside the ledger
it corroborates), deliberately NOT on the persisted `DeviceQueue` DTO: it never reaches
the queue JSON files. In-memory on the DI-singleton manager, a plugin/process boot begins
with every flag clear by construction, which IS the "clear on plugin startup" rule (a
restart-persisted active flag would read as truth on a fresh boot and re-launch stale
audio, the exact bug class). `InteractionDiagnostics.SincePlaybackStarted` was evaluated
as the reuse candidate and REJECTED: its records are gated behind the opt-in
`DiagnosticInteractionLogging` toggle (default off), so as-is it is empty in production;
un-gating it would also make a static dictionary load-bearing across test classes
(ClearAll cross-talk under xUnit class parallelism).

**Lifecycle rules:** `PlaybackStartedEventHandler` sets the flag (unconditionally, not
under the diagnostics toggle; the shared `(_queueManager ?? Plugin.Instance?.DeviceQueueManager)`
idiom). `PlaybackStoppedEventHandler` clears it, and `PlaybackFinishedEventHandler` and
`PlaybackFailedEventHandler` clear it as well (a finished/failed stream is not an active
one; the queued-next advance re-sets via its own Started). Every clear is
DISPLACEMENT-EXEMPT: a terminal event naming a stream other than the device's latest
start (a newer PlaybackStarted already owns the device) must NOT clear, because the
device is actively playing the newer stream; the coordinator review tightened this
further for the SAME-ITEM shape (see the review round below): item identity alone
cannot distinguish two streams of one item, so same-item re-launches mint a launch
generation into their stream token and the classifier compares generations. The read
side is `PlaybackLaunchBuilder.IsAudioPlaybackActive(context, queueManager)`:
the event flag OR the request context's own `playerActivity` report (PLAYING /
BUFFER_UNDERRUN, the existing `IsActivelyPlaying` definition). The context arm is what
keeps the genuinely-playing on-device case working even mid-re-launch windows.

**Which gates now require it:** ONLY the speed re-launch
(`SetPlaybackSpeedIntentHandler`, right after the JF-632 VideoApp refusal): medium==Audio
AND no active signal yields the existing `NoMediaPlaying` Tell (the e2e row's expected
answer). The loop family and the sleep timer were scoped OUT, deliberately, because their
PINNED suites encode current-evidence semantics the flag-alone policy would violate:
`ApplyRepeatModeAsync` already runs the JF-629 idle guard (no token AND no session item
-> no-media) and its pins `HandleAsync_TokenSurvivesPlaybackStopped_StillAppliesMode` /
`HandleAsync_NoTokenSessionDtoEvidence_AppliesModeToSessionItem` mandate that a surviving
token / session now-playing entry IS current evidence (the pause shape); the sleep timer's
own entry guard (`FullNowPlayingItem == null` -> NoMediaPlaying, which every pinned arming
test exercises) already refuses the cold shape before any re-issue. Neither has the
unbounded-ledger-only hole the speed handler had (its item resolve's ledger tail was its
ONLY guard). The task brief's blanket "loop family apply / sleep-timer arming" wording
would have broken those pinned tests, and the brief itself says a pinned failure is a
design violation to fix: the fix is this scoping.

**Residual, accepted:** a server session whose stop report was lost (the JF-581
write-loss class) can still hold a stale now-playing entry that admits the sleep-timer
re-issue or a loop mode write; that is server-state staleness, not the ledger-tail bug,
and is bounded by session idle expiry. Also, on a PAUSED device (our pause path sends
AudioPlayer.Stop -> PlaybackStopped clears the flag) a speed ask now answers the honest
no-media Tell instead of resuming at the new rate: say "riprendi" then the speed ask.

**Coordinator gate-marker review round (applied as the review(jf655) commit):**
(1) SAME-ITEM DISPLACEMENT GAP, APPLIED: the directive stream token is the bare item
id, so a same-item re-launch (speed re-launch, sleep re-issue, repeat-one) could never
classify as displacement; with Started(new) delivered before the displaced old
stream's terminal event, the clear darkened the flag while audio played. Fix:
`StreamTokenCodec` mints a per-launch generation suffix (`|launch:{n}`, composable
with `|sleep:`), `PlaybackReportOrdering.BeginStart` records the started stream's
generation, and `IsDisplacementStop` compares generations for same-item stops (a bare
stop against a nonced start is the older generation; a start with no generation keeps
the item-only rule). The mint is CONDITIONAL (`PlaybackLaunchBuilder.MintStreamToken`):
only a launch replacing the ACTIVELY-PLAYING stream of the same item nonces its token,
so every ordinary directive keeps the bare id dozens of pinned suites assert. Minted
at the BuildAudioPlayerResponse chokepoint, the sleep arm, and the sleep cancel
replay. Pinned by `SpeedRelaunch_LateOldStreamStopAfterNewStart_KeepsFlagActive`
(the exact live shape), `ColdDevice_SpeedDirective_KeepsTheBareItemToken`, and the
StreamTokenCodecTests launch facts.
(2) VACUOUS BOOT-CLEAR TEST, APPLIED: `AudioActiveFlag_FreshManager_BootsClear` now
marks and records through a manager on ONE temp directory, disposes it (flushing the
persist), re-creates a manager on the SAME directory, and asserts the LEDGER entry
reloaded while the flag reads clear (the earlier form used a second directory, which
could not detect the flag being persisted at all).
(3) TASK-DOC CONTRADICTION, APPLIED: the Lifecycle paragraph above now states the
displacement exemption instead of the pre-review "unconditional" wording.
(4) PARALLEL STORE, ACCEPTED (no restructuring): `_activeAudioPlaybackDevices` stays a
separate manager-level store rather than deriving from PlaybackReportOrdering's
LastStart/PendingStop (static, token-keyed, and per-instance test isolation is the
point of the DI-side home); the dictionary's doc comment now carries the drift guard
(a new terminal-event path must update BOTH stores) and this note records the
acceptance.

**Round-3 coordinator review (applied as the review(jf655) round 3 commit):**
(1+3) RAW TOKEN CONSUMERS, APPLIED. The generation suffix broke every consumer that
parsed or compared the raw AudioPlayer token: ShuffleOnIntentHandler and
ShuffleOffIntentHandler (raw Guid.TryParse; a suffixed token made the physical
reshuffle/restore silently skip while speaking success; both now parse through
StreamTokenCodec and pass the PARSED id to ShuffleRemaining/MoveTo, whose queue
lookups compare bare ids) and ResumeIntentHandler (two raw Ordinal equality guards,
the queue-candidate skip at ~:211 and the book branch at ~:389; both now compare
through the new StreamTokenCodec.NamesItem(token, itemId), the ONE comparison helper)
plus LaunchRequestHandler's stale-token check (~:219-220, also NamesItem). SWEEP of
every Guid.TryParse and token-equality site in the plugin: fixed the four above;
left deliberately, each because its input is NOT a stream token: SleepTimerIntentHandler's
raw fallback (its codec arm runs first; the fallback only sees unsuffixed unparseables);
ProgressReporter :250/:512, LaunchRequestHandler :158/:249, FollowMe, Fallback/Yes
resume-state ids, ListPagination, LibraryFilter, AplUserEvent (queue/ledger/session/list
stores, bare by construction); NextTrackPrecomputeCache :121 (compares its OWN stored raw
token to the current raw token, same generation by construction); the chokepoint's
ExpectedPreviousToken echo (verbatim previous token, correct as-is); all Controller and
StreamTokenHelper/LastPlayedResponseInterceptor parses (URL-space ids, never directive
tokens); PlaybackLaunchBuilder's ledger parses (bare stores).
(2) MINT GATE, APPLIED: MintStreamToken now gates on IsAudioPlaybackActive itself
(flag OR context player report), the SAME evidence the re-launch gates accept, so a
re-launch admitted via the context arm alone (plugin restart mid-playback) still mints;
this also resolves finding 7 (the mint calls the shared gate instead of re-implementing
the manager+device-key resolution, the tightest possible non-drift form; the resolved
values are internal to the shared call). Consequence, one JF-655-added test adjusted:
the round-2 ColdDevice_SpeedDirective_KeepsTheBareItemToken pin asserted the bare
token under a PLAYING context with the flag clear, the exact premise finding 2 removes;
it is now PlayingContextReport_FlagClear_SpeedDirective_MintsGeneration (same shape,
inverted assertion), and the bare-side pin moved to StartOver_IdleDevice_KeepsTheBareItemToken
(idle device, no evidence at all).
(4) SAME-ITEM DISPLACEMENT POSITION, APPLIED: PlaybackReportOrdering.ClassifyStop
returns the three-way kind (Real / DisplacedDifferentItem / DisplacedSameItem;
IsDisplacementStop delegates), and the Stopped handler reports the event's own raw
offset for the same-item kind (the item's real position at displacement for
identity-rate streams; composing through the launch scope would use the NEW stream's
base/rate, the wrong generation's) while the different-item zeroing keeps the JF-447
rationale.
(5) TEST-VS-PRODUCTION DIVERGENCE, APPLIED: the same-item re-launch call sites now
thread their manager into the chokepoint (StartOver ctor param, ProgressReporter's
queue advance, both NearlyFinished enqueue sites, JumpToPosition, SkipForwardBack x2;
Repeat already threaded), and the non-speed mint shape is pinned by
StartOverRelaunch_LateOldStreamStopAfterNewStart_KeepsFlagActive. Left on the
Plugin.Instance singleton fallback (production-correct; the divergence is test-only
and each site's launch is cross-item by construction): GoToChapter and the remaining
first-play handlers.
(6) TORN-WRITE HARDENING, APPLIED (trivial under the existing machinery): the latest
start publishes as ONE immutable volatile StartMarker(Info, LaunchGeneration) record,
so a concurrent classifier can never read the new start paired with the old
generation; BeginStart is the single writer.
(7) covered by (2).

**Review gates:** /simplify (4 parallel angles) returned clean; two minor items skipped
with reasons (the inline DeviceID chain matched 12 pre-existing sites in the same file
and was then fixed anyway under code-review; the gate-after-item-resolve one-bounded-read
on a cold refusal path is cheaper than splitting the JF-632/JF-655 gate block or flipping
the adjacent deleted-VideoApp-item response shape). /code-review high returned 6 findings:
APPLIED (2): the displacement-order clear race (the OLD stream's terminal event arriving
after the NEW stream's PlaybackStarted must not clear the flag; now guarded by
PlaybackReportOrdering.IsDisplacementStop in all three terminal handlers, pinned by
AudioActiveFlag_DisplacedStopAfterNewStart_KeepsDeviceActive) and the device-key idiom
drift (the reader now goes through the ONE GetDeviceId() extension like the writers).
TRACKED, not fixed (4): (1) the flag never expires when playback ends with NO terminal
event at all (the default-music-service takeover / device power-loss shapes, the
documented zero-PlaybackStopped evidence): the re-launch then still fires for that class;
a TTL is a heuristic wrong in both directions (resurrects the bug or refuses genuine
8h+ audiobook playback), and context corroboration (flag AND context not idle) would
refuse the transient mid-voice-pause shape the flag exists to cover; this is a strict
narrowing of the pre-fix behavior, not a regression. (2) medium==Unknown with
token/session evidence bypasses the new gate (empty ledger + stale token or a stale
server session): the task's design scoped the gate to medium==Audio; extending to
Unknown is the candidate follow-up (no pinned test blocks it; the mandate did not ask).
(3) no live device probe of the real Started-then-speed ordering: the e2e environment
fires no events by construction, so the positive path is pinned handler-level only; a
mid-playback device probe is Paolo's call. (4) the paused-device refusal line
('Nessun contenuto in riproduzione') is factually wrong for the paused shape: a
paused-shape string would need 17-locale additions, which this task's constraints forbid
(no locale strings); if it grates on-device, file it with new strings.

**Verification tail (round 3):** build 0 errors 0 warnings both TFMs; full suite green
both TFMs (summary line per TFM: `Passed!  - Failed:     0, Passed:  4525, Skipped:     0, Total:  4525`
= the 4523 after round 2 + the 2 StartOver pins). Finding 2's mandated gate change
collided with six stale pre-existing pins that assert BARE directive tokens under
PLAYING contexts (TestHelpers.CreateContextWithToken defaults playerActivity=PLAYING):
five in RepeatIntentHandlerTests and one in VideoAppGapHonestResponseTests
(Next_DuringAudio_QueueAdvance, whose shape legitimately mints); each was converted to
the codec item-naming assertion (the test's actual intent, "the directive replays THIS
item"), the same conversion class as the JF-447 composite-token migration, and the one
round-2 JF-655 test whose premise finding 2 removed was rewritten to pin the NEW
semantics (PlayingContextReport_FlagClear_SpeedDirective_MintsGeneration). Every other
pre-existing test is unmodified and green. New pins: stale-ledger+cold -> refusal (the e2e row's shape), flag-set -> re-launch,
PLAYING-context -> re-launch, VideoApp+flag -> still refuses, flag lifecycle (Started sets,
Stopped/Finished/Failed clear, fresh manager boots clear). VideoApp behavior byte-identical
(no VideoApp code path touched). The orchestrator runs the isolated e2e row
('a velocità uno e mezzo' -> 'Nessun contenuto in riproduzione') after merge+deploy; the
fixture row already carries that expectation (tests/integration/fixtures/e2e_it-IT.yaml,
unchanged by this task).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-27 as merge 17376cdb (pushed; deployed with the full checklist, config intact): the per-device active-audio flag (event-owned via PlaybackStarted/Stopped/Finished/Failed, displacement-exempt, boots clear by construction since it is never persisted) gates the speed re-launch, so a speed request with nothing playing is the honest 'Nessun contenuto in riproduzione' refusal instead of restarting stale audio. Three review rounds shaped it: (1) the per-launch generation nonce in the composite stream token, CONDITIONALLY minted only on same-item active replacements (keeping bare tokens everywhere the pinned suites assert them), with generation-aware displacement classification fixing the same-item re-launch hole; (2) the raw-token consumer migration to StreamTokenCodec (shuffle pair, resume guards, launch check: suffixed tokens resolve to their item everywhere, swept with the deliberate-left list documented); (3) the unified flag-OR-context mint gate, the same-item displacement position carry (the old stream's real offset, not the different-item zeroing), the atomic StartMarker, and the manager threading at every same-item re-launch site. Gates: /simplify clean, code-review skill twice (4 then 7 findings, all applied or dispositioned; the six stale token asserts converted to codec item-naming form under the JF-447 precedent, the collision disclosed), suites 4525/4525 both TFMs (orchestrator-verified on the final state, retried once under memory pressure). VERIFIED LIVE: the isolated e2e row passes on the deployed build (the cold refusal); the loop family and sleep timer scoped out with pinned-test evidence (they own current-evidence guards the flag would wrongly refuse).
<!-- SECTION:FINAL_SUMMARY:END -->
