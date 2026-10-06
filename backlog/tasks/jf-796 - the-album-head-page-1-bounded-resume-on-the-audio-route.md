---
id: JF-796
title: >-
  JF-796 - the album head's page-1-bounded resume on the audio route (the JF-793
  Finding 4 album twin)
status: In Progress
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
