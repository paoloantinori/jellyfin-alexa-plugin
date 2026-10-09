---
id: JF-796
title: >-
  JF-796 - the album head's page-1-bounded resume on the audio route (the JF-793
  Finding 4 album twin)
status: Done
assignee: []
created_date: '2026-10-06'
updated_date: '2026-10-06'
labels:
  - bug
  - playback
dependencies:
  - JF-793
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/AlbumPlayService.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/ResumeMath.cs
priority: low
---

## Description
<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-793 /simplify altitude round (2026-10-06), same-turn per the
review-recommendation rule. JF-793 Finding 4 closed the page-1-bounded resume on the
audiobook head (FindResumeTrackIndex scanned only the 5-item initial page, so deep
UserData progress relaunched from chapter 1); the closing note recorded "the album
precedent is not liftable (JF-625 criterion 3 is the video-route tracker
override)" - but that rationale covers only the VIDEO-route tracker override.
AlbumPlayService's head (~line 664) runs the SAME page-1-bounded
ResumeMath.FindResumeTrackIndex on the album's initial page for the AUDIO route
(resumePosition: false, index only), so an album whose UserData progress sits on a
track beyond the initial page (track 12 of 20) also relaunches from track 1 on a
fresh ask. Same defect class, different head.

Fix shape: mirror the JF-793 bounded deep resolution (when the page yields no
position and InitialPageHasMore says the album extends beyond the page, fetch the
album unpaged once and re-run the ONE resume decision on the full list, re-slicing
the page at the position-holding track), with the album head's own continuation
bookkeeping rebased accordingly. Watch the JF-625 seek-mode tracker arm: it walks
the PAGE items to map the tracked position onto the track timeline, so a re-sliced
page must keep that walk coherent (the tracker override reads positions, not
indexes). Red pin: deep album progress beyond the initial page, fresh ask, expect
the position-holding track to launch.
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

## Final Summary

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

N/A justifications: #4 no session-attribute writes touched (the QueueContinuation
DTO shape is unchanged, only its values); #5 no HttpClient touched; #6/#8 no
interaction-model or speech changes (the fix is resume bookkeeping only); #7 E2E
per the worker split (JF-793/JF-795 precedent: deep UserData progress beyond the
initial page cannot be set up deterministically against the live server, so the
defect class is pinned at the handler path, AlbumDeepResumeTests).

THE DEFECT AND THE FIX. AlbumPlayService.BuildAlbumPlayResponseAsync's audio
route ran ResumeMath.FindResumeTrackIndex over only the 5-track initial page, so
deep UserData progress (track 22 of 26) relaunched from track 1 on a fresh ask.
The fix mirrors the JF-793 bounded deep resolution in the album's own idiom:
when the page scan yields no position and InitialPageHasMore says the album
extends beyond the page, ONE unpaged scoped fetch runs (the pre-existing
BuildScopedAlbumTracksQueryUnpaged, reusing the page's working ParentId/AlbumIds
arm through the new pageUsedAlbumIds flag so a malformed-folder album's deep
folder query cannot silently come back empty), the ONE resume decision re-runs
on the full list, a position beyond the page re-slices the page at the
position-holding track (Skip/Take, startIndex 0), the continuation rebases
(StartIndex = deepIndex + re-sliced page count against the fetch-all honest
total, judged by the two-int InitialPageHasMore), and the new
deepResumePrefixTicks leads albumStartTicks so a COLD-tracker seek route still
slices the concat at the correct album-absolute position (pinned: 21 x 4min =
84min, not 0).

THE TRACKER VETO (the JF-625 interplay the task flagged). The deep block is
guarded by !trackerOverrideEngaged, set inside the criterion-3 override whenever
a warm tracker exists on the video route (mapped or not): the tracker stays the
resume truth there, UserData can never overtake it, and the walk itself is
byte-identical (one bool assignment added). Pin: a warm tracker at 5min with
deep UserData on track 22 keeps the tracker's track 2 / 4min+60s answer with no
re-slice.

SHARED VS OWN IDIOM (the task's step-1 decision): the fold into a shared helper
was EVALUATED and DECLINED; the album fix landed in the album's own idiom. The
sites diverge on four axes the books do not carry: the tracker veto guard, the
resumePosition:false + no-device-queue rescan, the AlbumIds fetch arm, and the
absolute concat prefix output (the books' offset is chapter-relative). The
shared primitives each site consumes (FindResumeTrackIndex, the scoped unpaged
builders, InitialPageHasMore) are already single-definition. The altitude
reviewer's residual (the ~25-line re-page algorithm now exists twice; JF-797
item 3 must touch both) is filed as JF-803, the lift to do alongside that
discriminator.

KNOWN TRADE, stated in the block comment as the books do: every first-ever ask of
a multi-page album pays the one unpaged fetch and finds nothing; the JF-797 item
3 discriminator applies (its addendum now records the album-side row-volume
sharpening from the code-review round).

RED PROOFS (unmodified tree, both TFMs, recorded pre-fix failure messages):
DeepProgressBeyondInitialPage_ResumesAtPositionHoldingTrack failed with
"Expected: <track-22 guid> / Actual: <track-1 guid>" (Assert.Equal on the
directive token); DeepProgressBeyondInitialPage_ContinuationOffsetAtResumePoint
failed with "Expected: 26 / Actual: 5" (the continuation stored the page-1
offset); the fresh-album trade assertion failed with "The collection did not
contain any matching items" (no unpaged query existed). Post-fix: 5429/5429
both TFMs (baseline 5421 + 8 new pins: 6 in AlbumDeepResumeTests, the warm-
tracker veto and the cold-tracker absolute-prefix pins in
AlbumAnnounceVehicleTests); Release -warnaserror 0 warnings 0 errors.

GATES. /simplify: 5 applied (the two-int InitialPageHasMore, the shared
TestHelpers.GetPlayDirective, the dead usings, the WarmTrackerFiveMinutesIn
hoist, the AssertSharedQueryShape extraction closing the cited lockstep drift),
1 skipped with reason (the cross-file paging-mock dedup, the JF-465 fixture
class, noted in the JF-803 filing), efficiency angle clean. /code-review high:
F3 and F4 applied as pins (the cold-tracker seek route; the end-unknown regime
transition plus its empirically-verified premise pin), F1 filed as JF-804 (the
pre-existing JF-625 tracker-walk page bound, out of scope by this task's
video-route-unchanged contract), F2 tracked via the JF-797 addendum (the
contract-sanctioned trade, row-volume sharpening recorded). Filings: JF-803,
JF-804, the JF-797 addendum. Production surface changed: deploys.
<!-- SECTION:NOTES:END -->
