---
id: JF-447
title: >-
  PlaybackReportOrdering residual interleavings: start-vs-start zombie,
  correction re-validation, displacement queue-state trust, double-stop (+
  test/registry hygiene)
status: To Do
assignee: []
created_date: '2026-09-02 00:14'
updated_date: '2026-09-02 22:00'
labels:
  - code-review
  - playback
  - follow-up-family
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Playback/PlaybackReportOrdering.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Event/PlaybackStartedEventHandler.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Event/PlaybackStoppedEventHandler.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/EventHandlerTests.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-ups from the JF-425 code-review round (2026-09-02, 7 CONFIRMED): the superseding-stop guard fixes the original start-vs-stop scenario (tested green) but leaves adjacent interleavings of the same zombie class open, plus hygiene items. Findings 1 (Finished/Failed missing the displacement guard) and 4 (composite sleep-token Guid crash) were fixed in the same night by a follow-up agent; this task tracks the REST.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 F3 start-vs-start: a stalled older start report landing after a newer start's report completed writes the old item over the new one with NO correction (the slot was cleared by the newer BeginStart after the older report dispatched). Candidate shapes: a per-device monotonic start generation the report must match to write (the 'epoch' the implementer collapsed because it was identical FOR STOP CORRECTION - it is NOT identical for start-vs-start); or re-validate after the report's await
- [ ] #2 F6 correction re-validation: the corrective OnPlaybackStopped is never re-checked after its own await; a newer Started(B) whose write lands during the correction's in-flight stop gets cleared by the stale stop when the correction completes
- [ ] #3 F2 displacement-state trust: the displacement classification reads DeviceQueueManager state that several play paths never populate (PlaySongIntentHandler never SetQueue: 'play album A1' then 'play song S' misclassifies A1's displacement stop as real). Either populate the queue on every play path or classify displacement without the queue
- [ ] #4 F5 double-stop: the correction cannot tell whether the recorded stop's own OnPlaybackStopped completed (RecordStop precedes its own report) and can fire a concurrent second OnPlaybackStopped (duplicate SaveUserData transaction, duplicate activity entries)
- [ ] #5 Tests for each interleaving (the review's scenarios are the test matrix)
- [ ] #6 Below-cap batch: Task.Delay absence checks -> completion seam; 2 unconverted inline handler constructions -> CreateStopHandler; deviceId extraction 4+8 inline copies -> shared extractor; fifth parallel per-device static registry -> shared slot abstraction; loop-toggle ordering exemption documented at the LoopOn/LoopOff/LoopSongOn call sites
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-02 additions from the review-round fix agent (relayed same-turn): (a) fold the displacement classification INTO RecordStop (taking raw token + queue) so the invariant is structural, not a three-handler caller-side protocol - skipped while the mechanism was under active review, natural first step here; (b) the composite sleep-token format has THREE owners (mint SleepTimerIntentHandler:~132, suffix parse PlaybackNearlyFinishedEventHandler:~83, prefix parse PlaybackFailedEventHandler.ParseItemId) - one shared StreamToken codec should migrate all sites; (c) SIBLING of the fixed Guid crash: PlaybackFinishedEventHandler:~71, PlaybackStoppedEventHandler:~105, PlaybackStartedEventHandler:~84 still call new Guid(req.Token) on the same composite tokens - a sleep-timer track that finishes/stops/starts kills those handlers before RecordStop and before the keep-alive ack (the same INVALID_RESPONSE class).

JF-424.1 worker observations (2026-09-02): (1) PlaybackStoppedEventHandler ~:130 still writes queue.CurrentItemId = req.Token unconditionally, the same dangling-pointer shape fixed in PlaybackNearlyFinished's cache-hit branch, but intentional there (resume-after-pause position store) and pre-existing; worth covering in this task's displacement/trust sweep. (2) The precompute cache-hit gate reads session.PlayState for shuffle/repeat while ResolvePlaybackOrder prefers the device queue: a device queue marked Shuffle with stale session PlayState could let the gate pass a stale-order serve (mitigated by the shuffle handlers now invalidating on toggle); same queue-state-trust family.

review-local gate (2026-09-02, score 75, below reporting threshold but real): the JF-424.1 cache-hit validation resolves the current item FullNowPlayingItem-FIRST (FindCurrentQueueIndex) while the STORE side (PlaybackStartedEventHandler.TryPrecomputeNext) resolves token-only; when a start report failed and session.FullNowPlayingItem still holds the PREVIOUS queue item Z, the validation computes Z's successor (=A, the playing track), rejects the cached B, falls through to full resolution which ALSO resolves Z and enqueues A after itself (JF-409 self-reenqueue class); pre-JF-424.1 the unconditional cache hit served B correctly in this state. Fix direction: resolve token-first when a cache entry exists (match the store side).
<!-- SECTION:NOTES:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining)
- [ ] #11 Findings applied or tracked
<!-- DOD:END -->
