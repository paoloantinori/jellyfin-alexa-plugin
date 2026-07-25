---
id: JF-270
title: 'Verify Follow Me device transfer (pull model, offset-0 by design)'
status: To Do
assignee: []
created_date: '2026-06-08 09:31'
updated_date: '2026-07-25 12:17'
labels:
  - e2e
  - playback
  - multi-device
  - testing
milestone: m-4
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/FollowMeIntentHandler.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
FollowMeIntentHandler transfers playback between Alexa devices using a PULL model: the user speaks to the DESTINATION device ("follow me"), which finds the most recently active queue from another device via DeviceQueueManager.GetAllActiveQueues and resumes it locally.

CRITICAL CORRECTION to original task: the code does NOT resume from the same position. FollowMeIntentHandler.cs:133-136 builds the stream URL with NO offset param, and the comment explicitly states "offset 0 since we don't track per-device playback position through DeviceQueueManager." Transfer resumes the current item from the beginning. This is a by-design limitation, not a bug to fix here.

What CAN be automated (unit tests, no hardware): the queue-selection logic, the source-clear behavior, the empty-queue response, and the directive construction. These are the deterministic, currently-untested code paths.

What CANNOT be automated (requires 2 physical Echos): actual audio handoff, source-device audio actually stopping, and offset-0 resume observed on-device. The simulator and SMAPI simulate-skill have no notion of a second device, so these are manual-only and should be recorded as pass/fail observations in the task notes, not blocking CI gates.

Handler behavior to verify (FollowMeIntentHandler.cs):
- null DeviceQueueManager → FollowMeNothingPlaying + warning log (line 78-82)
- no other active queues → FollowMeNothingPlaying + info log (line 89-93)
- picks most-recently-modified queue from other devices (line 85-96)
- SetQueue transfers queue to current device (line 117-122)
- GetStreamUrl with NO offset → offset-0 resume (line 133-136, known limitation)
- FollowMeSuccess speech with title (line 148-151)
- Clear source device queue (line 154)

Existing tests: NONE — FollowMeIntentHandler has zero test coverage today.
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
- [ ] #8 Locale response strings added to all 12 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-25 12:17
---
Session 2026-07-25: verified FollowMeIntentHandler already has 16 passing unit tests (FollowMeIntentHandlerTests.cs) covering queue selection, null/empty-queue handling, source-clear, directive construction, and now the offset-0 limitation (FollowMe_ResumesAtOffsetZero_ByDesign) and missing-item path. Hardened FollowMe_PicksMostRecentlyActiveQueue against DateTime.UtcNow tick-resolution flakiness by setting LastModifiedUtc explicitly. Corrected the misleading 'at the stored offset' class comment (the code resumes at offset 0) and documented the limitation in README. Remaining: AC #7-8 (2-Echo hardware test) are manual-only.
---
<!-- COMMENTS:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Unit test (NEW, automatable): FollowMeIntentHandler picks the MOST RECENTLY MODIFIED queue from other devices when multiple exist — inject a fake/mock DeviceQueueManager returning two queues with different LastModifiedUtc, assert the later one's item is played
- [ ] #2 Unit test (NEW, automatable): FollowMeIntentHandler returns FollowMeNothingPlaying when no other device has an active queue (GetAllActiveQueues returns empty)
- [ ] #3 Unit test (NEW, automatable): FollowMeIntentHandler returns FollowMeNothingPlaying when DeviceQueueManager is null (DI not provided)
- [ ] #4 Unit test (NEW, automatable): handler CLEARS the source device queue after transfer (SetQueue called on current device, Clear called on source device) — assert via mock verification
- [ ] #5 Unit test (NEW, automatable): the response contains an AudioPlayer.Play directive pointed at the source queue's current item, AND the FollowMeSuccess speech (title interpolated)
- [x] #6 Document the KNOWN LIMITATION honestly in code/docs: transfer resumes at offset 0, NOT at the saved playback position (DeviceQueueManager tracks per-item resume position, not cross-device transfer offset) — this is by-design, not a bug
- [ ] #7 Hardware verification (manual, requires 2 Echos): start playback on device A, say 'ask <invocation> to follow me' on device B, confirm the current track resumes on B and device A stops — record the result (pass/fail + offset-0 observed) in the task notes
- [ ] #8 Hardware verification (manual): after transfer, confirm voice commands (pause/next) work on device B from the transferred queue
- [x] #9 Update the task title/description if 'resume from same position' phrasing remains anywhere — that behavior does not exist in code
<!-- AC:END -->
