---
id: JF-221
title: End-to-end testing and certification preparation
status: To Do
assignee: []
created_date: '2026-05-25 20:11'
labels: []
milestone: m-3
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
End-to-end integration testing and certification preparation.

**Test scenarios:**
1. "Alexa, play [song name] on Jellyfin" — plays specific song with progress bar
2. "Alexa, play [artist name] on Jellyfin" — plays all songs by artist
3. "Alexa, play [album name] on Jellyfin" — plays album tracks
4. "Alexa, next" / "Alexa, previous" — queue navigation
5. "Alexa, shuffle on" / "Alexa, loop on" — queue modes
6. "Alexa, stop" / "Alexa, pause" — playback control
7. Device handoff — move playback between Echo devices
8. Unknown artist/song — graceful "not found" response
9. Long playback session (30+ min) — verify no stream expiry issues

**Certification checklist:**
- [ ] All required directives implemented (GetPlayableContent, Initiate, GetNextItem)
- [ ] Error handling returns proper Alexa error response format
- [ ] Response times under 2 seconds
- [ ] Stream URLs accessible from Amazon's infrastructure
- [ ] Privacy policy URL provided
- [ ] No personally identifiable information logged
- [ ] Skill works in US English (en-US)

**Note on geographic availability:** Music skills are currently for US distribution only. International support requires separate registration.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 End-to-end test: 'play X on Jellyfin' triggers GetPlayableContent -> Initiate -> playback with progress bar
- [ ] #2 Next/previous voice commands work via GetNextItem/GetPreviousItem
- [ ] #3 End-to-end test on Echo Show shows progress bar and elapsed time
- [ ] #4 Skill passes Amazon's Music Skill validation tests
- [ ] #5 Deployment guide verified on clean Docker environment
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
