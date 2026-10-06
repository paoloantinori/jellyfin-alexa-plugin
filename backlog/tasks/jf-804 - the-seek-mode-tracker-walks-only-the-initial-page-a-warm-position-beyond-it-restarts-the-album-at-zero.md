---
id: JF-804
title: >-
  JF-804 - the seek-mode tracker walk scans only the initial page, so a warm
  tracker position beyond the page's runtime restarts the album at zero
status: To Do
assignee: []
created_date: '2026-10-07'
labels:
  - bug
  - playback
dependencies:
  - JF-796
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/AlbumPlayService.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/AudiobookPlayResolver.cs
priority: low
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
