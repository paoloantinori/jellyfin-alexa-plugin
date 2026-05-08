---
id: JF-68
title: >-
  Fix duplicate album display and missing artist in AudioPlayer metadata
  subtitle
status: Done
assignee: []
created_date: '2026-05-04 18:39'
updated_date: '2026-05-05 14:45'
labels:
  - bug
  - ui
  - echo-show
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
On Echo Show devices, the album name appears twice — once in the APL full-screen subtitle ("Artist · Album") and again in the AudioPlayer metadata bar subtitle (just "Album"). On non-screen devices, the subtitle only shows the album name, missing the artist info entirely.

**Fix:** Change `BaseHandler.GetSubtitle()` (line 255-268) to return the artist name for audio items instead of the album name. This gives:
- **Non-screen devices**: AudioPlayer subtitle shows artist — users see "Track by Artist" in the Alexa app
- **Screen devices**: AudioPlayer bar shows artist, APL shows "Artist · Album" — no duplication

**File to modify:** `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs` — the `GetSubtitle(BaseItem?)` method should use `audio.Artists?.FirstOrDefault()` for audio items, mirroring the pattern already used in `AplHelper.GetSubtitle()`.

**Do NOT change** `AplHelper.GetSubtitle()` — it already correctly shows "Artist · Album".
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 AudioPlayer metadata subtitle shows artist name for audio tracks
- [x] #2 APL full-screen subtitle still shows 'Artist · Album'
- [x] #3 TV episode subtitle still shows series name (no regression)
- [x] #4 No duplicate album name between AudioPlayer bar and APL screen
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Changed `BaseHandler.GetSubtitle()` to return `audio.Artists[0]` for audio items (falling back to `audio.Album` when no artists), instead of always returning album. Episode path unchanged (SeriesName). Added 4 unit tests covering: artist present, artist absent with album fallback, neither present (empty), and episode series name. All 20 CoverArtTests pass. AplHelper.GetSubtitle() was NOT changed — it already shows "Artist · Album". Commit: bb4b216
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->
