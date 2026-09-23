---
id: JF-623
title: >-
  Echo Show shows a stale track during music playback (Magnolia playing, screen
  showed the previous session's Adele track)
status: To Do
assignee: []
created_date: '2026-09-23 17:04'
updated_date: '2026-09-23 18:24'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-23 device detail (Paolo): the stale screen was FULL-SCREEN, not the Echo's bottom now-playing bar. That rules out the stream-metadata widget hypothesis (it renders the small bar UI). A full-screen display can only be (a) our APL NowPlaying document rendered by an earlier play and never replaced, or (b) a VideoApp.Launch surface. Live config shows AplVisualsEnabled unset/false, so today's plays attach nothing; the document on screen must date from a play made while visuals were on (or from the VideoApp audio route if NativeControlsForAudio was on at some point). Next diagnostic step when at the device: note the exact moment a fresh music play starts and whether the screen changes AT ALL; then check whether enabling AplVisualsEnabled makes the card track the current track (the attacher path exists but is carousel-only - extending TryAttachNowPlayingDirective to the BuildAudioPlayerResponse chokepoint is the likely fix either way, since a fresh card per play would also mask the stale-document problem).

2026-09-23 follow-up: the full-screen detail plus config facts narrow it down. NativeControlsForAudio=True globally but DefaultVideoAppForAudio=None (off): both the Adele (PlayFavorites) and Magnolia (FindSong) plays pass the item through BuildAudioPlayerResponse, so either BOTH route VideoApp (then the screen would track the title, contradiction) or the delegation is off for both. Since neither play logged a RenderDocument and the APL visuals flag is off, our code rendered NOTHING on either play: the full-screen surface must be the Echo Show's OWN AudioPlayer full-screen player, which persists across plays when the stream carries no metadata (Alexa.NET's AudioItemStream has no metadata support - probe found no metadata/title/art surface on it). Fix direction: extend TryAttachNowPlayingDirective to the BuildAudioPlayerResponse chokepoint (our APL card on every music play, full control of the display); requires AplVisualsEnabled on, so the launch behavior becomes config-dependent - Paolo should decide whether he wants the screen card back on.
<!-- SECTION:NOTES:END -->

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
