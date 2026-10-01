---
id: JF-683
title: >-
  JF-683 - the PlaySong-fallback artist queue never continues (silent guard skip
  at the prefetch window: no fetch, no log; PlaybackFinished calls
  queue-exhausted 2 tracks early)
status: In Progress
assignee: []
created_date: '2026-09-30 16:45'
labels:
  - playback
  - progressive-queue
  - bug
dependencies: []
references:
  - >-
    backlog/tasks/jf-666 -
    JF-666-artist-plays-stop-at-the-initial-5-track-page-the-precompute-fast-path-starves-the-continuation-and-the-fetchers-JF-358-query-shape-silently-returns-zero.md
  - >-
    backlog/tasks/jf-674 -
    JF-674-stale-queue-continuations-inject-mid-playlist-content-into-a-later-unrelated-single-item-playback-no-queue-identity-validation.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn from Paolo's live device round (18:30-18:40, log-verified on the JF-678 build).

THE LIVE EVIDENCE: the phrase "musica di norah jones" was routed by NLU to PlaySongIntent (nondeterministic vs the morning's PlayArtistSongsIntent routing of the same semantic request); PlaySongIntentHandler.PlayArtistSongsFallback -> CrossMediaFallback.BuildArtistSongsResponseAsync logged "PlaySong fallback: fetched 5 songs for artist='Norah Jones'" at 18:30:44. Norah has 13 tracks, artistItems.Count=5 == GetInitialFetchSize(), so the continuation Set gate at CrossMediaFallback.cs:381-396 should have stored (SourceType "Artist", StartIndex 5). At 18:39:55 the PlaybackNearlyFinished fired with current=track 3 of 5 (remaining = 5-2-1 = 2 = the live PrefetchThreshold of 2, so the remaining guard passes IF the index resolves); the handler completed in 10.9ms from the precompute cache hit with ZERO library lookups and NO fetch INFO/WARN line - one of TryFetchContinuationBatch's three silent guards returned: (1) continuation == null (the Set never happened for this key, or something removed it between 18:30 and 18:39), (2) FindCurrentQueueIndex < 0 (the token f692e13c not found in the session queue view), (3) remaining > threshold (only if the queue view disagreed with the 5-item queue the Started handler reported). Corroborating symptom: at 18:40:03 the PlaybackFinished handler logged "queue exhausted, ending session" while TWO tracks remained to play (track 4 started at 18:40:04) - the same queue-view/index confusion shape.

THE WORK: (a) reproduce in a unit test driving BuildArtistSongsResponseAsync (the PlaySong-fallback label path) then a NearlyFinished with a 5-item queue at track 3: assert the fetch fires (or find the failing guard and fix it); (b) OBSERVABILITY: the three silent guards in TryFetchContinuationBatch (continuation null / index < 0 / remaining > threshold) each return without a Debug line - add per-guard LogDebug naming which guard skipped and the values (continuation present?, resolved index, queue count, remaining, threshold) per the debug-logging policy; the "queue exhausted" Finished log should also name its view (queue count + index) so a mis-view is diagnosable from logs alone; (c) fix whichever guard actually fails on the fallback path ( suspicion: FindCurrentQueueIndex's SessionQueue view vs the queue the fallback path built, given the corroborating exhausted-early symptom).

VERIFICATION: the new unit pin (fallback path + NearlyFinished at remaining==threshold fetches and the queue grows past 5); the guard-debug lines visible in a live-shaped run; the existing JF-666 pins and the PlayArtistSongs-path pins stay green; full suite both TFMs. Live spot check on the next device round: the PlaySong-fallback phrase should burn the catalogue like the direct path does.

## RESOLUTION (2026-09-30, worker investigation)

THE PREMISE IS REFUTED BY THE LIVE LOGS IT CITED. The full 18:30-18:50 window was
re-read on the live box (podman logs, request bodies included). Corrected timeline
(track ids: t1=27605075 "Wish I Could", t2=f692e13c "Sinkin' Soon", t3=bf12cb6c "The
Sun Doesn't Like You", t4=6384e62b "Until the End"):

- 18:30:44 PlaySong fallback: queue 5, continuation Set (fetched 5 >= gate 5; the
  Set gate at CrossMediaFallback.cs:381 is `>=`, no off-by-one).
- 18:35:01 NearlyFinished(t1): index 0 of 5, remaining 4 > threshold 2. Legit skip.
- 18:35:09 Finished(t1): "queue exhausted, ending session" (playerActivity=FINISHED;
  t2 started 18:35:10, one second later). RACE OCCURRENCE 1.
- 18:39:55 NearlyFinished(f692e13c): that token is t2, NOT track 3. Index 1 of 5,
  remaining 3 > 2. A LEGITIMATE threshold skip, not a guard failure. The filing
  misread the token; "10.9ms cache hit, no fetch line" is the correct behavior at
  this position.
- 18:40:03 Finished(t2): "queue exhausted" again (t3 started 18:40:04). RACE 2.
- 18:42:54 NearlyFinished(t3): remaining 2 == threshold: "Progressive queue: fetched
  8 items for Artist (offset 5/end-unknown)" FIRED, queue grew 5->13, cache-hit
  served t4. THE CONTINUATION MACHINERY WORKS on the PlaySong-fallback path.
- 18:43:02 Finished(t3): "queue exhausted" again (t4 started 18:43:03). RACE 3.
- 18:43:03 Started(t4): "queue position 5/13" - the 13-item queue is live.
- 18:46:01 Paolo PAUSED (AMAZON.PauseIntent, offset 175s of a 236s track - the pause
  card reads "2:58 di 3:56"), preempting t4's NearlyFinished window (~228s) by ~52s;
  PlaybackStopped 18:46:04. No missing NearlyFinished anomaly.
- 18:50:22 SessionEnded (UserInitiated) + PlayRadioIntent: Paolo manually switched.

So: no guard failed (guard 1 excluded - the Set fired and nothing removed the entry,
proven by the 18:42:54 fetch; guard 2 excluded - index resolution worked at 18:42:54;
guard 3 fired only at positions where it SHOULD). The round ended by user action,
not starvation. The SILENCE of the guards did real damage though: this task was
filed with a wrong root-cause hypothesis because a legit threshold skip was
indistinguishable in the logs from a guard bug.

THE REAL DEFECT (the "exhausted early" corroborating clue, promoted to primary):
PlaybackFinished's hasQueuedNext gate reads ONLY context.AudioPlayer.PlayerActivity.
All three live Finished requests carried playerActivity=FINISHED during the ~1s
inter-track gap before the ALREADY-ENQUEUED next stream started, so the handler
logged "queue exhausted" and ended the session (dismissing the APL screen) at every
track boundary. Observable consequence in the same round: the 18:46:01 pause arrived
sessionNew=true because the race had killed the session at 18:43:02.

FIX LANDED:
1. PlaybackFinishedEventHandler: hasQueuedNext now ALSO survives when the finished
   item (event token, composite-safe via StreamTokenCodec) has a successor in the
   session queue - the plugin's own queued-next view bridging the activity gap. True
   exhaustion (no successor, not actively playing) still ends the session (pinned).
2. Observability (JF-683 mandate): TryFetchContinuationBatch's three skip guards
   each LogDebug their name and values (continuation present?, resolved index, queue
   count, remaining, threshold, source), plus a proceed line at the fetch point; the
   Finished "queue exhausted" line names its view (queueCount, finishedIndex,
   playerActivity).
3. NO change to FindCurrentQueueIndex resolution order or the Set gate: both were
   proven healthy by the live round; changing them would be a drive-by refactor
   against evidence.

PINS: PlaybackNearlyFinished_PlaySongFallbackQueue_AtPrefetchBoundary_FetchesAndGrowsQueue
(green regression pin through the REAL CrossMediaFallback builder path - red was not
achievable because the machinery works; the live log is the proof),
PlaybackNearlyFinished_ThresholdGuardSkip_LogsGuardNameAndValues (guard-debug
observability, asserts the remaining value),
PlaybackFinished_InterTrackGapWithQueuedSuccessor_KeepsSessionAlive (RED on base
code, proven: session ended at track 3 of 5 with a queued successor; green with the
fix; also asserts the keep-alive debug line),
PlaybackFinished_LastItemNoSuccessor_EndsSessionAndLogsView (true exhaustion keeps
ending + the log names its view).

REVIEW ROUND (same day): /simplify 4-angle pass applied 7 findings (threshold read
hoisted once so the logged value is provably the compared one, the successor scan
computed only on the not-actively-playing path, the Guid.Empty ternary dropped
(IndexOfQueueItem already answers -1), file-local handler factories gained the
optional logger param, shared TestHelpers context/play-directive helpers adopted,
LogLevel unqualified); 2 skips justified (a shared arrange-act helper for the two
Finished tests would bury the one varying axis; a QueueOf builder for 3 one-line
queue sites adds a symbol for negligible gain). The JF-579 roster test
(SessionQueueReaderRoster_MatchesAssemblyScan) caught the new Finished queue read on
the first full-suite run EXACTLY as designed: PlaybackFinishedEventHandler adopted
the shared ProgressReporter.TryRehydrateSessionQueueFromDevice bridge at entry
(listed in AdoptedReaders; behavioral pin
PlaybackFinished_RestartWipedSession_CoherentDeviceQueue_KeepsSessionAlive in
QueueRehydrationAdoptionTests). /code-review high: 5 findings - 3 applied (the
loop-mode arm: RepeatOne/RepeatAll enqueue a NON-adjacent successor at the last
position so loop mode counts as queued-next; the expired-sleep-timer carve-out: the
one shape where NearlyFinished deliberately enqueued nothing; the rehydration
adoption above), 2 test-strengthening findings applied (remaining value asserted;
keep-alive debug line pinned), 2 semantic corners declined with reasons and filed
SAME-TURN as JF-691 (unreshuffled-shuffle last position; deleted-successor lingering
screen - both pre-existing at base, both needing a policy hoist beyond this diff).

REWORK ROUND (same day, orchestrator gate-marker F3/F4/F5; F6 process-only, nothing
to do): F3 - the carve-out and loops arm now have their own pins:
PlaybackFinished_ExpiredSleepToken_AtBoundary_EndsSessionAndLogsCarveOut (composite
sleep token past deadline at a boundary -> session ENDS + the carve-out line logged),
PlaybackFinished_FutureSleepToken_AtBoundary_KeepsSessionAlive (future deadline ->
the successor arm still keeps alive), PlaybackFinished_LoopModeAtLastPosition_
KeepsSessionAlive (RepeatOne AND RepeatAll Theory at the last position -> keeps
alive via the loops arm). Red proofs by branch inversion: sleepExpired/loops forced
false -> ExpiredSleep + both LoopMode cases FAIL (3), FutureSleep passes; the
helper's deadline comparison dropped -> FutureSleep FAILS on both TFMs. (The first
red attempt hit the documented --no-build trap: stale DLLs passed; rerun with a
fresh build.) F4 - the carve-out logs its own Debug line ("sleep timer expired ...
NearlyFinished enqueued nothing; ending the session despite the queue view"),
killing the finishedIndex=-1 triage misread on a populated queue. F5 - the
expired-deadline predicate is ONE definition now:
StreamTokenCodec.IsSleepExpiredUtc(token, DateTimeOffset now), routed through BOTH
NearlyFinished's enqueue gate and Finished's carve-out (the F3 pins are its parity
tests); NearlyFinished keeps its "Sleep timer expired" INF line.
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

<!-- SECTION:FINALSUMMARY:BEGIN -->
Completed 2026-09-30. #4/#5/#8 N/A unchanged (no session-attribute DTOs, HttpClient,
or locale strings touched). #6 N/A (no interaction-model change). #7: five unit pins
landed (the fallback-path continuation pin through the real builder, the guard-debug
line, the inter-track-gap keep-alive RED->GREEN, the true-exhaustion view log, the
JF-579 rehydration adoption pin); a live device round is the standing verification
for on-device behavior (the observability lines make the next round readable).
Suites 4789/4789 both TFMs (baseline 4784 + 5). Residual corners filed as JF-688.
<!-- SECTION:FINALSUMMARY:END -->
