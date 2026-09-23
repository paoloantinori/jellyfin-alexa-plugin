---
id: JF-623
title: >-
  Echo Show shows a stale track during music playback (Magnolia playing, screen
  showed the previous session's Adele track)
status: To Do
assignee: []
created_date: '2026-09-23 17:04'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live observation 2026-09-23 18:41 (Paolo's loop test): while Magnolia (Negrita) played via a FindSong play, the Echo Show screen still showed "Set Fire to the Rain" (Adele), the track played at 13:59:40 via PlayFavorites. Evidence gathered: the 18:41:52 FindSong play response carries ZERO RenderDocument directives; TryAttachNowPlayingDirective is only called from AplUserEventHandler (carousel taps), NOT from the regular play paths; live config has AplVisualsEnabled unset/false (VisualsEnabled: None), so no APL cards render from anywhere today. Open question: which screen was it (our APL full-screen player from an earlier era, or the Echo's own now-playing widget fed by stream metadata we do not set on music plays). If the latter, the fix is setting AudioItem Stream metadata (title/artist/art) on music launches so the device widget tracks the actual track.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 While a FindSong/PlayRandom music play is active on the Echo Show, the screen shows the CURRENT track (not the previous session's)
- [ ] #2 Root cause identified: whether it is our APL card not refreshing (AplVisualsEnabled is currently OFF in live config, so no cards render at all and the Echo keeps its last display), the Echo's own AudioPlayer now-playing widget not updating without stream metadata, or a stale VideoApp route
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
