---
id: JF-520
title: >-
  JF-514 follow-ups: resume-tail rebase + shared resume-resolve helper; evaluate
  seed-binding (delete the DTO flag); hoist the 5x test seam; weigh the UserData
  raw-offset seed-classification note
status: Done
assignee: []
created_date: '2026-09-07 21:50'
updated_date: '2026-09-07 23:51'
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
- [x] #1 Tail adoption: ResumeIntentHandler's read side rebases device-derived offsets like YesIntentHandler (base+offset when a base is recorded, drop only as fallback), replacing the interim drop; announce path uses the effective offset
- [x] #2 The shared resume-resolve helper extracted to BaseHandler beside ResolveAudioLaunchSource (probe, ledger read, rebase-or-drop, single resolve) so the probe-before-resolve ordering rule becomes structural instead of comment-enforced at each caller
- [x] #3 Re-evaluate the seed-binding alternative (simplification finding): binding the rebase at the offer seed in HandleResumeOfferAsync would delete the OffsetIsStreamRelative DTO field and the Yes-side gate (~-50 lines); verify no future flow can mint a new base while an offer session stays open, then adopt or reject with the reasoning recorded
- [x] #4 Test-seam hoist: TestEpisodeWithStreams/TestMovieWithStreams/TestStream now exist as 5 copies across 4 test files; hoist into Unit/TestHelpers.cs and update the pre-existing files
- [x] #5 Weigh the cross-angle correctness note from the simplify pass: PlaybackStoppedEventHandler persists RAW device offsets into server UserData (lines ~69/103/141), so the UserData/server-progress seeds' item-absolute classification (OffsetIsStreamRelative=false) is not always true for items previously played audio-shaped through the transcode; if confirmed, either add base at that writer or re-classify those seeds
- [ ] #6 Full suite green; /simplify + code-review high gates before merge
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped: BaseHandler.ResolveResumedAudioLaunch owns probe + ledger read + rebase-or-drop + single resolve, making the read-before-write ordering STRUCTURAL (was comment-enforced at call sites). The resume tail adopted the rebase (interim drop deleted): a paused-then-resumed transcode-routed item now continues from base+offset instead of restarting at 0, and each resolve records the new base so the next cycle composes. The device-last-played (UserData) seed classifies at seed time (ledger-has-base AND routes-to-transcode; ledger-first operand order gates the codec DB probe). Seed-binding REJECTED with a code-recorded refutation (event handlers resolve queue items without ending the session, so a wrapped queue can clobber an offered item's base inside the offer window - equivalence unprovable). knownAudioCodec threading removes the resolve's second media-streams DB read. Test seam hoisted to TestHelpers (5 copies removed). Gates: /simplify 2-agent combined (applied E1/E2/S1/S6; skipped with reasons R3 fixture-consolidation, S5 cosmetic dedup, S2 taste), code-review high SAFE TO MERGE (all three tail fallbacks walked clean, codec threading verified end-to-end incl. fail-open, concurrency cleared, the 4 new tests verified to fail under the old behavior; F1 residual filed as JF-521, F2 recorded there). Tests 3470/3470 on branch and main post-merge (364dd432, branch 32c1245e). Deployed to minix with JF-519 in the same DLL (symbols verified in the running binary, config intact, play + resume smoke paths clean, no errors). DoD 5-8 N/A.
<!-- SECTION:FINAL_SUMMARY:END -->

## Implementation Notes (2026-09-08, branch fix/jf520-resume-followups)

Deliverables 1-5 implemented; suite 3470/3470 (baseline 3466 + 4 new tests), Release build
clean with -warnaserror. Gates (/simplify, code-review high) left for the orchestrator per
the dispatch instructions.

**AC#1+#2 (tail adoption + shared helper):** `BaseHandler.ResolveResumedAudioLaunch` now
sits beside `ResolveAudioLaunchSource` and owns the whole shape: probe routing, read the
ledger base, rebase base+offset when stream-relative with a base, drop to 0 when
stream-relative without one, pass through when item-absolute, then ONE resolve. The
ledger read is structurally INSIDE the helper immediately before the resolve that
overwrites it (the JF-514 comment-enforced ordering is now impossible to violate at a
caller). Consumers: YesIntentHandler's confirm (passes `resumeState.OffsetIsStreamRelative`)
and ResumeIntentHandler's tail (passes true unconditionally; fallbacks 1-3 are all
device-derived per the tail's own doc, and fallback 4 returns before the tail resolve).
A paused-then-resumed transcode now continues from base+offset instead of restarting at 0.

**AC#3 (seed-binding): REJECTED**, reasoning also recorded as a code comment at the Yes
gate. The equivalence premise "no non-launch handler resolves between offer and confirm"
is false: the AudioPlayer EVENT handlers (PlaybackStartedEventHandler ~line 339,
PlaybackNearlyFinishedEventHandler ~line 214) call ResolveAudioLaunchSource for the NEXT
queue item without ending the session, and on a wrapped queue (repeat-one / one-item) the
resolved next item IS the offered item, clobbering its ledger base to 0 inside the offer
window. With the flag shape the confirm then reads the clobbered base (a real race, though
it favors seed-binding); with seed-binding the offer would have captured the pre-clobber
base. Either way the two shapes are NOT behaviorally equivalent, so the "prove equivalence
then adopt" bar is not met. Secondary reason: deleting the serialized
`offsetIsStreamRelative` field is a contract change with a rolling-deploy transient
(pre-deploy offers deserialize post-deploy losing provenance). The flag and the gate stay;
the gate body shrank to one helper call.

**AC#5 (UserData raw-offset seeds): re-classified at the seed (option chosen over adding
the base at the writer).** `BuildDeviceLastPlayedOffer` (the UserData reader; also reached
via the NativeControlsForAudio stale-token path) now classifies at seed time:
`offsetIsStreamRelative = RoutesToAudioTranscode(item) && GetAudioTranscodeBase(deviceId,
itemId).HasValue`. A recorded base is the transcode signature: raw-static audio launches
record base 0 (rebase = no-op), VideoApp plays and other clients' progress record nothing
(item-absolute pass-through stands), AudioBooks never route to the transcode (playlist
resume unaffected). Fallback 4 in ResumeIntentHandler needed NO change, verified
mechanically: its video branch (Movie/Episode) returns a VideoApp launch built from
GetVideoAppLaunchUrl and never calls ResolveAudioLaunchSource (ledger writes exist ONLY at
BaseHandler:1416/1423), and its audio branch serves only Audio/AudioBook via raw
GetStreamUrl; neither branch can mint a ?start. BuildScreenlessAudioFallbackOffer likewise
offers Audio/AudioBook only, so its flag=false is genuinely correct. Known residual
(documented at the seed and in ResumeHelper): UserData is cross-client, so a transcode
launch followed by a later play of the same item on another client leaves a stale base and
the rebase would add it; the position sources cannot distinguish that corner. Accepted as
the cost of fixing the common audio-shaped case.

**AC#4 (test-seam hoist):** `TestHelpers.TestStream` / `TestHelpers.TestEpisodeWithStreams`
/ `TestHelpers.TestMovieWithStreams` in Unit/TestHelpers.cs; the 5 private copies removed
from YesIntentHandlerTests, ResumeIntentAudioVariantOffsetTests (x2),
PlayVideoIntentHandlerTests, ResumeConfirmationTranscodeBaseTests; the inline
`new MediaStream {...}` shapes in Yes/PlayVideo tests also now use the factory.

**Tests added (4):** tail rebase via AudioPlayer context and via DeviceQueue
(ResumeIntentAudioVariantOffsetTests; both pin the minted ?start=base+offset AND the
post-mint ledger base, which implicitly pins the helper's read-before-resolve ordering);
device-last-played offer seeds flag=true with a recorded base and composes end-to-end on
confirm (ResumeConfirmationTranscodeBaseTests), and stays flag=false without a base. The
pre-existing tail tests (drop when no base, raw-static pass-through) keep passing
unchanged: the no-base rule matches the old interim drop.

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
