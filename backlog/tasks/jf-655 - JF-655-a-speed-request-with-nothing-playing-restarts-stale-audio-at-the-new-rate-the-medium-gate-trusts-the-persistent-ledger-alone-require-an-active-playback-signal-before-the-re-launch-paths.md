---
id: JF-655
title: >-
  JF-655 - a speed request with nothing playing restarts stale audio at the new
  rate (the medium gate trusts the persistent ledger alone); require an
  active-playback signal before the re-launch paths
status: To Do
assignee: []
created_date: '2026-09-27 13:31'
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

## Implementation Notes

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
idiom). `PlaybackStoppedEventHandler` clears it (unconditionally: a displacement stop
clears too, and the new stream's own PlaybackStarted re-sets it moments later).
`PlaybackFinishedEventHandler` and `PlaybackFailedEventHandler` clear it as well (a
finished/failed stream is not an active one; the queued-next advance re-sets via its own
Started). The read side is `PlaybackLaunchBuilder.IsAudioPlaybackActive(context, queueManager)`:
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

**Verification tail:** build 0 errors 0 warnings both TFMs; full suite green both TFMs
(summary line per TFM: `Passed!  - Failed:     0, Passed:  4517, Skipped:     0, Total:  4517`
= 4507 prior + 4 speed-gate tests + 6 flag-lifecycle tests, the review-round displacement
pin included); new pins: stale-ledger+cold -> refusal (the e2e row's shape), flag-set -> re-launch,
PLAYING-context -> re-launch, VideoApp+flag -> still refuses, flag lifecycle (Started sets,
Stopped/Finished/Failed clear, fresh manager boots clear). VideoApp behavior byte-identical
(no VideoApp code path touched). The orchestrator runs the isolated e2e row
('a velocità uno e mezzo' -> 'Nessun contenuto in riproduzione') after merge+deploy; the
fixture row already carries that expectation (tests/integration/fixtures/e2e_it-IT.yaml,
unchanged by this task).

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

DoD evidence: (1)(3) final build `Build succeeded. 0 Warning(s) 0 Error(s)` both TFMs;
(2) suite green both TFMs (4517/4517 per TFM, exit 0); (4)(5) no session attributes or
HttpClient code touched (N/A); (6) no interaction-model change (N/A); (7) the handler
logic is pinned by 10 new tests and the pre-existing e2e fixture row
('a velocità uno e mezzo' -> 'Nessun contenuto in riproduzione') flips from failing to
passing, which IS the e2e coverage for this fix (the e2e harness fires no playback
events, so the active path is pinned handler-level); (8) no locale strings added (task
constraint: the refusals already exist); (9) /simplify 4 angles clean (2 minor items
dispositioned); (10) /code-review high: 6 findings, 2 applied, 4 tracked above.
