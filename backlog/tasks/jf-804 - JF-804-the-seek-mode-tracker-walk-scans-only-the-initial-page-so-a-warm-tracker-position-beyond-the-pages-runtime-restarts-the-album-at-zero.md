---
id: JF-804
title: >-
  JF-804 - the seek-mode tracker walk scans only the initial page, so a warm
  tracker position beyond the page's runtime restarts the album at zero
status: Done
assignee: []
created_date: '2026-10-07'
updated_date: '2026-10-07 08:42'
labels:
  - bug
  - playback
milestone: m-18
dependencies:
  - JF-796
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/AlbumPlayService.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/AudiobookPlayResolver.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed by the JF-796 worker (2026-10-07), same-turn per the review-recommendation
rule, from the /code-review high round's finding 1. PRE-EXISTING and deliberately
out of JF-796's scope (its contract pinned the video/seek route unchanged), but
the review sharpened why it matters now.

The defect: AlbumPlayService.BuildAlbumPlayResponseAsync's JF-625 criterion-3
tracker override maps the tracker's album-absolute position onto the track
timeline by walking ONLY the initial page's items (the runtime-prefix loop). On
Echo Show with NativeControlsForAudio on, a user hours into a long album (tracker
holds the concat segments under the album GUID; the page carries ~5 tracks of
runtime) produces a tracked position that never satisfies
`tracked < prefix + runtime` within the page: trackedIndex falls out at
albumItems.Count, the guard skips the startIndex assignment, trackedInTrackTicks
stays 0, and the launch mints the concat URL with start=0. Playback restarts at
track 1, 0:00, silently losing hours of position. The book twin has no such hole:
AudiobookPlayResolver slices the playlist by the tracker's ABSOLUTE ticks
directly (no page-bounded mapping).

JF-796's tracker veto makes the new deep-resume machinery structurally unable to
fix this as built (the veto skips the deep fetch whenever the tracker engaged,
mapped or not, which is the correct criterion-3 precedence: UserData must never
overtake a warm tracker), so the fix belongs INSIDE the tracker arm: when the
walk falls off the page end, resolve the position against the unpaged album (the
JF-796 deep-fetch shape, tracker-keyed instead of UserData-keyed) and map it
there, re-slicing the page at the tracker's track. Consider riding the JF-803
re-page lift so the unpaged fetch is shared. Red pin: warm tracker beyond the
page on a 26-track seek-mode album, fresh ask, expect the tracker's track at its
absolute offset, not track 1 at 0.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session-attribute changes)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no model changes)
- [x] #7 E2E test added for new intent or handler logic (N/A: no new intent; the worktree does not deploy, the E2E axis is covered by the new unit pins)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new strings, existing logs only)
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Final Summary
<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed by the JF-804 worker (2026-10-07). The fix lives INSIDE the tracker arm
of AlbumPlayService.BuildAlbumPlayResponseAsync exactly as the filing scoped:
when the runtime-prefix walk falls off the page end and continuationHasMore,
the album is fetched once unpaged through QueueContinuationFetcher.
BuildScopedAlbumTracksQueryUnpaged with the page's working ParentId/AlbumIds arm
(pageUsedAlbumIds), the walk re-run on the full list (the ONE shared
WalkTrackerOntoTrack helper, extracted from the former inline loop), and on a
deep landing the page re-slices at the tracker's track with the JF-796 re-page
bookkeeping (startIndex 0, trackedInTrackTicks, deepResumePrefixTicks from the
sibling's Take(deepIndex).Sum form, continuation rebased to deepIndex + slice
against the honest total). The JF-796 tracker veto is untouched: the UserData
deep-resume gate stays under !trackerOverrideEngaged, so UserData can never
overtake a warm tracker and at most one deep fetch runs per request. A position
beyond even the full list (or an empty deep fetch) stays cold: the page's own
resume answer stands, logged honestly (code-review F1). JF-803's re-page lift
was NOT ridden (still To Do); the decline and the resulting three-copy census
are recorded on JF-803 (same-turn, the /simplify + /code-review shared finding).

RED PROOF (both TFMs, on the unmodified tree):
AlbumAnnounceVehicleTests.SeekModeAlbumResume_TrackerBeyondPageRuntime_
DeepFetchMapsOntoTheTrack - 26-track seek-mode album (4 min/track), tracker
warmed to 5130s (514 segments, conservative read), fresh ask; pre-fix FAILED
with "start=51300000000 not found" (the launch minted NO start offset: track 1
at 0:00, the defect). Post-fix the launch slices at the tracker's absolute
position (start=5130s ticks = the 21-track 84-min prefix + the 90s in-track
partial), the metadata names Track 22, the page re-slices to tracks 22..26, no
continuation remains, and the query pin shows exactly one unpaged tracks query
with ZERO user-data probe queries (the veto shape).

Companion pins: the in-page tracker mapping (SeekModeAlbumResume_
TrackerPosition_MapsOntoTrackAndOffset), the cold-tracker deep resume
(SeekModeAlbumResume_ColdTracker_DeepResumeSlicesTheConcatAtTheAbsolutePrefix),
and the tracker-still-wins veto (SeekModeAlbumResume_WarmTracker_
VetoesTheDeepUserDataResume) all pre-existed and stay green; the boundary
shapes (exact page end, exact track boundary, exact full end, one tick, zero
runtimes) verified against the walk by hand.

Gates: /simplify 4 findings applied (declaration comment trim, the walk tuple
shrunk to (index, in-track), the deep-miss log truth, the JF-803 census) + 2
skips recorded (the books-gate null-check collapse: sibling-gate idiom match;
the in-file paging-mock hoist: JF-803's filed work, census updated same-turn).
/code-review high: no correctness bugs, 4 findings applied (the deep-miss log
wording, the self-checking Sum prefix form, the WarmTrackerAt doc floor truth,
the both-flags-off ordering pin) + 1 dispositioned as already tracked (the
third re-page copy IS the JF-803 census). Suites: touched battery 160/160 both
TFMs at the final state; Release -warnaserror 0/0 both TFMs; full suite
5490/5490 both TFMs (baseline 5484 + 6).
<!-- SECTION:FINAL_SUMMARY:END -->
