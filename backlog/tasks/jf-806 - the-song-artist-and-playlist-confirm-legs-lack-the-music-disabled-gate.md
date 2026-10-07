---
id: JF-806
title: >-
  JF-806 - the song, artist, and playlist confirm legs lack the music-disabled gate
  (the confirm-must-match-ask gap left open while books, podcasts, and albums gated)
status: To Do
assignee: []
created_date: '2026-10-07'
labels:
  - tech-debt
  - playback
dependencies:
  - JF-805
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/YesIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-805 round (2026-10-07), same-turn per the
review-recommendation rule.

YesIntentHandler's disambiguation confirm legs are gated inconsistently: the
book leg gates BooksEnabled (JF-611, adopted in JF-795), the podcast leg gates
PodcastsEnabled (JF-611), and the MusicAlbum leg gates MusicEnabled (JF-805),
but the SONG, ARTIST, and PLAYLIST switch arms still launch with no gate at
all. Their direct asks all gate at entry under JF-467 (PlaySong,
PlayArtistSongs, PlayPlaylist via IfMediaTypeDisabled(c => c.MusicEnabled)),
so a "yes" on a song/artist/playlist prompt that was opened before an admin
disabled music launches media the direct ask would refuse, answering
differently from the ask on the disabled axis, the exact class JF-611/JF-795/
JF-805 closed for the other three media types.

FIX SHAPE: the same three-line IfMediaTypeDisabled(c => c.MusicEnabled,
request) block the JF-805 MusicAlbum branch carries, applied to the three
ungated switch arms (or folded into whatever shared confirm-leg seam a future
round extracts; the JF-805 altitude review noted three such gate blocks now
exist in HandleAsync and a Task-returning gate wrapper could own them once).
Playlists are cross-type by content but the PlayPlaylist ask gates on music
today, so the confirm should match whatever the ask answers; verify the ask's
gate before copying it. Pin per leg the same way
HandleAsync_DisambiguationAlbumType_MusicAlbumConfirm_MusicDisabled_
AnswersMediaTypeNotAvailable pins the album leg.
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

JF-805 GATE-MARKER CORRECTIONS (2026-10-07, same-turn): (1) PREMISE FIX - the playlist leg's premise is WRONG: PlayPlaylistIntentHandler does NOT gate at entry on MusicEnabled (read end to end; zero IfMediaTypeDisabled/MusicEnabled hits in it, ShufflePlayIntentHandler, and BuildPlaylistPlayResponseAsync), and playlists are documented cross-type always-allowed (BaseHandler.IsTypeAllowed). Gating only the playlist CONFIRM arm would CREATE the confirm-vs-ask divergence the rule prohibits. RESCOPE: song+artist legs only (their asks do gate); the playlist leg needs the ask gated first if ever, as its own decision. (2) FOLDED AXIS (marker finding 2): the confirm path runs the ask's full cold-database surface with NO warming gate - the direct asks are Layer-1 gated (GuardIndexReady; WarmingGateCoverageTests), YesIntentHandler is absent from ExpectedGatedHandlers, and JF-805's routed composition widened the ungated work to the JF-796 deep fetch plus per-track UserData reads; the restart-mid-session confirm hits the cold DB inside the Alexa window (the JF-419 class). The confirm-leg seam round this task anticipates should carry the warming-gate axis for BOTH the book and album confirm legs (the JF-795 twin shares the shape).
