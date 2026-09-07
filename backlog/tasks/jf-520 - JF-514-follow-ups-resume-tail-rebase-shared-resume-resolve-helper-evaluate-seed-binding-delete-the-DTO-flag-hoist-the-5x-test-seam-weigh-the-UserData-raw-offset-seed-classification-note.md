---
id: JF-520
title: >-
  JF-514 follow-ups: resume-tail rebase + shared resume-resolve helper; evaluate
  seed-binding (delete the DTO flag); hoist the 5x test seam; weigh the UserData
  raw-offset seed-classification note
status: To Do
assignee: []
created_date: '2026-09-07 21:50'
labels:
  - resume
  - video
  - transcoding
  - cleanup
  - follow-up
dependencies: []
references:
  - JF-514
  - JF-507
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
JF-514 shipped the transcode-base rebase on the OFFER path only; ResumeIntentHandler's read-side tail still uses the interim drop (restart at 0), and three findings from the JF-514 four-agent /simplify pass (2026-09-07) were deliberately deferred rather than folded into the shipping diff:

1. TAIL ADOPTION + HELPER (the code comments in ResumeIntentHandler promise 'until this tail adopts it too'): extend the base+offset rebase to the resume tail; extract the shared resume-resolve helper (probe, ledger read, rebase-or-drop, single resolve) to BaseHandler so the load-bearing ordering constraint (the resolve WRITES the ledger, so reads must precede it) becomes structural instead of comment-enforced at each caller site.
2. SEED-BINDING ALTERNATIVE (simplification agent's structural finding): binding the rebase at the offer seed (HandleResumeOfferAsync) would delete the OffsetIsStreamRelative DTO field, its JSON property, the BuildResumeOfferResponse parameter, and the entire Yes-side gate (~-50 lines, one fewer serialized cross-session invariant). Trade-off: the decision binds at offer time instead of confirm time; today observably equivalent under the session model. Verify the equivalence argument, then adopt or reject.
3. TEST-SEAM HOIST: TestEpisodeWithStreams + Stream helper now exist as 5 copies across 4 test files (YesIntentHandlerTests, ResumeIntentAudioVariantOffsetTests x2, PlayVideoIntentHandlerTests, ResumeConfirmationTranscodeBaseTests). Hoist to Unit/TestHelpers.cs (kept out of JF-514 to avoid churn in 3 pre-existing files).
4. CORRECTNESS NOTE TO WEIGH (from the altitude agent, mechanism verified): PlaybackStoppedEventHandler persists raw device offsets into server UserData, so for an item previously played audio-shaped through the transcode, the server-progress seeds' item-absolute classification (flag=false) can be wrong; the rebase would then not apply where it should (or a future seed-binding change could double-count). Decide: add the base at that writer, or re-classify those seeds.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Tail adoption: ResumeIntentHandler's read side rebases device-derived offsets like YesIntentHandler (base+offset when a base is recorded, drop only as fallback), replacing the interim drop; announce path uses the effective offset
- [ ] #2 The shared resume-resolve helper extracted to BaseHandler beside ResolveAudioLaunchSource (probe, ledger read, rebase-or-drop, single resolve) so the probe-before-resolve ordering rule becomes structural instead of comment-enforced at each caller
- [ ] #3 Re-evaluate the seed-binding alternative (simplification finding): binding the rebase at the offer seed in HandleResumeOfferAsync would delete the OffsetIsStreamRelative DTO field and the Yes-side gate (~-50 lines); verify no future flow can mint a new base while an offer session stays open, then adopt or reject with the reasoning recorded
- [ ] #4 Test-seam hoist: TestEpisodeWithStreams/TestMovieWithStreams/TestStream now exist as 5 copies across 4 test files; hoist into Unit/TestHelpers.cs and update the pre-existing files
- [ ] #5 Weigh the cross-angle correctness note from the simplify pass: PlaybackStoppedEventHandler persists RAW device offsets into server UserData (lines ~69/103/141), so the UserData/server-progress seeds' item-absolute classification (OffsetIsStreamRelative=false) is not always true for items previously played audio-shaped through the transcode; if confirmed, either add base at that writer or re-classify those seeds
- [ ] #6 Full suite green; /simplify + code-review high gates before merge
<!-- AC:END -->

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
