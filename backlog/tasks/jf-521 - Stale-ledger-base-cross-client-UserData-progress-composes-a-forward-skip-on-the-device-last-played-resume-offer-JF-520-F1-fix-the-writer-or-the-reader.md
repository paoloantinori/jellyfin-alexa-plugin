---
id: JF-521
title: >-
  Stale ledger base + cross-client UserData progress composes a forward skip on
  the device-last-played resume offer (JF-520 F1); fix the writer or the reader
status: To Do
assignee: []
created_date: '2026-09-07 23:45'
labels:
  - resume
  - transcoding
  - known-issue
  - follow-up
dependencies: []
references:
  - JF-520
  - JF-514
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-520 code review (2026-09-08), finding F1 - CONFIRMED mechanism, live-reachable on this deployment (NativeControlsForAudio=true verified in the container config):

BuildDeviceLastPlayedOffer classifies a UserData position as stream-relative whenever the device ledger has a base for the item AND the item routes to the transcode. But UserData is CROSS-CLIENT: after the device did an audio-shaped transcode launch (base recorded, UserData stream-relative), a LATER play of the same item on the phone or via VideoApp advances UserData with an ITEM-ABSOLUTE position while the device ledger base stays stale. The offer then flags it stream-relative and the confirm composes base+position = a FORWARD skip of exactly the stale base (walk: base 20min + phone-watched 40min mints ?start=60min on a 40min-true position; no runtime clamp, so the mint can exceed the item length). The residual was documented and accepted when JF-520 shipped the seed classification as the cheapest fix for the common audio-shaped case; this task owns the real fix.

Also record from the same review, finding F2 (PLAUSIBLE, narrow, one-sentence note; no code change requested): a resolve whose directive never plays advances the ledger while the offset source stays frozen from the previous cycle (device offline / response lost, no AudioPlayer events), so each resume retry walks the mint forward by the stale offset; the same shape has existed on the offer path since JF-514.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Decision between fix shapes recorded with reasoning: (a) fix the WRITER (event handlers persist item-absolute positions by adding the launch base; could eventually delete the flag) or (b) fix the READER (clear/ignore the base when UserData was written by a non-audio-shaped source)
- [ ] #2 If (a): writer sites updated with tests; the seed classification re-evaluated; the residual re-checked end to end
- [ ] #3 If (b): invalidation implemented with tests; the F1 walk re-verified (stale base no longer composed)
- [ ] #4 Regression guard: the screenless-Dot common path (context seed, flag=true, rebase) unaffected
- [ ] #5 Full suite green; /simplify + code-review high gates before merge
<!-- AC:END -->

## Implementation Notes (2026-09-08, branch fix/jf521-stale-base-cross-client)

**AC#1 DECISION: (b) FIX THE READER**, implemented as a tick-exact provenance check at the
seed plus a hard runtime clamp in the shared rebase helper. Full reasoning (also recorded
as a code comment at the seed, LaunchRequestHandler.BuildDeviceLastPlayedOffer):

*Readers' audit that drove it.* The writers (PlaybackStoppedEventHandler; also Started/
Finished/NearlyFinished) persist the RAW device offset into FOUR stores: the server session
PlayState (via OnPlaybackStopped/OnPlaybackStart with PlaybackStopInfo.PositionTicks),
DeviceQueue.CurrentPositionTicks, DeviceQueue.ItemPositionState, and Jellyfin UserData
(fill-when-zero). Readers found: session.PlayState.PositionTicks feeds ResumeIntentHandler
fallback 2 (REBASES today), SkipForwardBack + GoToChapter (skip/chapter math, mints via the
RAW STATIC URL, already broken on transcode-routed items per the f0240020 incident class),
SleepTimer + MediaInfo + BaseHandler.BuildPositionDisplay (display only);
CurrentPositionTicks feeds ResumeIntent fallback 3 (REBASES today);
ItemPositionState feeds AplUserEventHandler.GetResumeOffset and FindResumeTrackIndex
(audiobooks; never transcode-routed); UserData feeds the F1 seed itself, the screenless
audio fallback, ResumeIntent fallback 4 (verified safe in JF-520), PlaySong resume, PlayVideo
resume (VideoApp), SortAndFindResumeIndex, BuildVideoLaunchSpeech, InProgressMediaList
(display), and every OTHER Jellyfin client (the cross-client sync write's audience).

*Why not (a) fix-the-writer.* (1) Blast radius: every compensating reader must flip in the
same change or double-add; most sharply the ResumeIntent tail, whose streamRelative=true is
UNCONDITIONAL for three fallbacks of which only two are plugin-written: fallback 1 (the
AudioPlayer context offset) is written by AMAZON and stays stream-relative forever, so the
tail would need per-fallback classification permanently. (2) The writer's precondition is
unsafe: converting raw to item-absolute at stop time needs the launch base AT EVENT TIME,
but the ledger is a last-RESOLVE ledger with a documented mid-playback clobber
(PlaybackNearlyFinished resolves the wrapped/repeat-one next item, which IS the current
item, at offset 0, zeroing its base during playback; the same race the JF-520 seed-binding
refutation recorded). A clobbered read would persist a WRONG item-absolute value. (3)
Severity: (a)'s errors land in server-PERSISTENT UserData (cross-client visible, survives
restarts, feeds Jellyfin's own resume UI), and a rolling deploy leaves pre-deploy
stream-relative values with no provenance flag (minted raw or double-added). A
misclassification under (b) costs one transient ?start=.

*The (b) shape.* The task's suggested comparison heuristic (UserData < base implies
item-absolute) does NOT close the F1 walk: the walk's own numbers have the phone position
(40min) ABOVE the stale base (20min), so it still composes 60min. The discriminator that
closes it: the stop event wrote the SAME raw ticks into BOTH UserData (via the server's
OnPlaybackStopped pipeline and the fill-in) and ItemPositionState, so UserData ==
GetRecordedDeviceOffsetTicks(device, item) iff the last UserData write was THIS device's
audio-shaped stop (stream-relative); any other value was advanced by another source
(item-absolute, no rebase). New classification (ledger-first operand order preserved, the
codec DB probe now runs LAST): base > 0 recorded AND positionTicks > 0 AND device raw ==
positionTicks AND RoutesToAudioTranscode(item). Plus the REQUIRED hard clamp in
BaseHandler.ResolveResumedAudioLaunch: a composed base+offset that reaches or exceeds the
item's runtime (when known) is never minted; the raw offset wins and it logs. A
legitimate composition cannot get there, because the stream-relative offset counts at
most the remaining runtime, so that shape means a stale base or foreign position. The
clamp also bounds F2's
retry walk (each retry composes base+frozen-offset; at the runtime it clamps and stops
growing).

*Honest residuals under (b), all bounded and conservative (restart earlier by the base,
never the F1 forward skip):* ItemPositionState trimmed (cap 200) or the queue cleared while
the base survives reads null (classifies item-absolute); a second Echo's stop also breaks
equality; a ms-exact coincidence (another client stopping at exactly the device's raw
offset) still composes, bounded by the clamp when past runtime. PlaybackFinished reports to
the server without an ItemPositionState write (finished items are typically Played/zeroed).
Known NOT fixed (pre-existing, out of scope, noted for the record): the writers' raw values
make the cross-client UserData display and MediaInfo/SkipForwardBack math wrong for
transcode-routed items: real latent gaps an (a)-shaped writer fix would also address, but
they do not require that risky cutover to fix later.

**AC#3 implementation:** LaunchRequestHandler seed classification as above;
BaseHandler.GetRecordedDeviceOffsetTicks (new, read-only via the DeviceQueueManager.GetItemPositionTicks accessor (pure _queues.TryGetValue, moved off GetQueue by the simplify pass), Guid->"N" key
normalization to match the writer's ItemPositionState keys; ledger keys stay dashed);
BaseHandler.ResolveResumedAudioLaunch clamp. Doc updates: the seed, BuildResumeOfferResponse
param, ResumeHelper.ResumeState.OffsetIsStreamRelative, ResumeIntentHandler class doc.

**Tests (ResumeConfirmationTranscodeBaseTests), F1 walk numbers:** device base 20:00
recorded; the device's own stop persisted a 5:00 raw offset into ItemPositionState; UserData
later advanced to 40:00 item-absolute by the phone.
- NEW DeviceLastPlayedOffer_UserDataAdvancedByAnotherClient_NotRebased_NoForwardSkip: offer
  flags item-absolute (not stream-relative), confirm mints ?start=40:00 and NOT 60:00.
  Verified failing under the old classification (git-stash run: flags true, mints 60:00).
- NEW ConfirmResume_ComposedStartReachesItemRuntime_ClampsToRawOffset: runtime 30:00, base
  20:00, stream-relative 15:00 -> composed 35:00 >= 30:00 -> clamps to raw 15:00; ledger
  records the minted 15:00. Verified failing pre-change (minted 35:00).
- UPDATED DeviceLastPlayedOffer_TranscodeItemWithRecordedBase_FlagsStreamRelativeAndRebasesOnConfirm
  (the JF-520 flag=true case): now seeds ItemPositionState = the UserData ticks, pinning
  the new provenance invariant; still composes 25:00 end-to-end.
- NEW AC#4 guard LaunchResumeOffer_FromAudioPlayerContext_OnTranscodeItem_ConfirmRebases:
  the common path exactly as JF-520 shipped it, end to end (context seed flags
  stream-relative unconditionally; base 20:00 + context 5:00 mints 25:00; ledger 25:00).
  Passes pre- and post-change by design (regression pin).

**Suites:** full run 3473/3473 (baseline 3470 + 3 new); Release build -warnaserror clean.
DoD 4/5 (DTO/BaseAddress): no new session-attribute fields, no HttpClient changes. DoD 6-8
N/A (no interaction-model, no new strings). DoD 9-10 left to the orchestrator's gates per
the dispatch.

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
