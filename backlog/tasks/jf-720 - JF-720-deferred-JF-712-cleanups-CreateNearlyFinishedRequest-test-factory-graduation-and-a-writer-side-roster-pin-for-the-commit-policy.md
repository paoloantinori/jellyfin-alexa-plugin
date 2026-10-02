---
id: JF-720
title: >-
  JF-720 - deferred JF-712 cleanups: CreateNearlyFinishedRequest test-factory
  graduation and a writer-side roster pin for the commit policy
status: To Do
assignee: []
created_date: '2026-10-02 23:59'
labels:
  - code-quality
  - testing
  - refusal-contract
dependencies:
  - JF-712
references:
  - >-
    backlog/tasks/jf-712 -
    JF-712-PlaybackNearlyFinished-advance-state-before-the-token-gated-build-a-refused-continuation-leaves-phantom-queue-pointer-and-zeroed-position.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the JF-712 /simplify and /code-review rounds, carrying
the two findings both rounds judged real but out of that task's scope:

1. TEST-FACTORY GRADUATION (simplify reuse round, borderline note): the private
   `CreateNearlyFinishedRequest(token, offsetMs)` AudioPlayer-event factory now exists
   as FIVE per-file copies (GaplessPlaybackTests, PreEnqueueOnStartTests, RadioModeTests,
   ProgressiveQueueTests, and JF-712's PlaybackNearlyFinishedRefusalTests). The per-file
   copy is the established convention and JF-712 followed it, but the fifth copy is the
   natural graduation point: TestHelpers already owns the analogous intent-request
   factory (CreatePlayCommand) and would be the home for one shared
   `CreateAudioPlayerEventRequest(type, token, offsetMs)`; the five private copies then
   collapse to delegations (or deletions). Low value, low risk, one file plus five
   call-site files.
2. WRITER-SIDE ROSTER PIN (simplify altitude round, finding 3, judged optional there):
   the JF-712 derive-then-commit policy in PlaybackNearlyFinishedEventHandler is
   enforced by behavioral pins per arm (PlaybackNearlyFinishedRefusalTests), but a
   future FIFTH pre-build queue write in that handler is guarded only by the class-doc
   claim. The repo has the deeper idiom twice (SessionQueueReaderRosterTests,
   DeliveredLaunchOutputSpeechRosterTests, both IlCallScanner IL rosters): a
   writer-side roster asserting that every session-queue MUTATION site in
   PlaybackNearlyFinishedEventHandler is exactly
   {CommitPendingContinuation, TryFetchContinuationBatch's documented pre-build
   append, the entry rehydration mirror} would make the exception list structural the
   way JF-699's output-speech roster did for the launch gates. Pin requirement: the
   roster must be self-red (adding a raw NowPlayingQueue write anywhere in the handler
   flips it) and the three allowlisted sites must carry their in-code BY DESIGN
   markers (two of the three landed in JF-712 already).
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
