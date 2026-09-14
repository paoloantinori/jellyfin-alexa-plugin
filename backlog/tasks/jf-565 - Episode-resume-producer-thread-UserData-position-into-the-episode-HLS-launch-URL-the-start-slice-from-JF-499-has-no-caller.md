---
id: JF-565
title: >-
  Episode resume producer: thread UserData position into the episode HLS launch
  URL (the ?start= slice from JF-499 has no caller)
status: To Do
assignee: []
created_date: '2026-09-14 22:38'
labels:
  - episode
  - resume
  - hls
  - videoapp
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up from JF-499 (2026-09-14): the episode remux endpoint now honors ?start=<ticks> on every serve path (ServeEpisodePlaylist with EXTINF-accurate slicing), but NOTHING in production mints a ?start= on the episode URL: BaseHandler.GetVideoAppLaunchUrl calls GetEpisodeVideoAudioUrl(item.Id) with no start (BaseHandler.cs:736-739), so the resume-slice machinery is dormant. Plumbing needed: (1) add the startTicks overload to GetEpisodeVideoAudioUrl (the sibling GetEpisodeAudioUrl(itemId, startTicks) at BaseHandler.cs:751-760 is the exact model); (2) thread a UserData-derived position through GetVideoAppLaunchUrl for Episode items (VideoApp has no offset, so the slice IS the resume mechanism); (3) note the W1 interplay: the tracker now deliberately skips leaf items (only Folder keys record), so EPISODE resume positions must come from Jellyfin UserData (PlayPositionTicks), not the segment tracker; (4) decide the Video-relaunch position UX: relaunching a movie via VideoApp also cannot seek, so the same ?start= slice does not apply (movies are not HLS-sliced) - scope this task to EPISODES only; (5) on-device verification: resume mid-episode on the Echo Show, seek bar must show the sliced-relative timeline (same known limitation as audiobooks). Related: JF-563 (the audiobook Resume/StartOver NativeControlsForBooks bypass - same plumbing family, book side).
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
