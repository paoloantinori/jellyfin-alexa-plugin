---
id: JF-661
title: >-
  JF-661 - the song-to-album cascade has no kana bar: PlaySong not-found
  reroutes a kana-origin query into TryAlbumFallbackAsync which plain-accepts at
  90
status: To Do
assignee: []
created_date: '2026-09-28 12:15'
labels:
  - search
  - i18n
  - ja-JP
  - acceptance
  - device-found
dependencies: []
references:
  - >-
    backlog/tasks/jf-654 -
    JF-654-the-song-side-kana-bar-is-missing-ビートルズ-refuses-the-wrong-artist-but-TrySongFallback-then-auto-plays-a-wrong-song-Bitters-Absolut-for-the-kana-derived-query.md
  - >-
    backlog/tasks/jf-660 -
    JF-660-JF-652-artist-side-kana-bar-inert-on-PlaySong-FindSong-entity-fallback-both-pass-pre-romanized-text-to-TryEntityFallbackAsync.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-28 from the JF-654 /code-review high round (finding 1), verified against AlbumPlayService the same turn.

THE GAP: JF-654 added the song-side kana bar at TrySongFallback, FindSong's chain, PlaySong's title fallback, and SearchMedia's song-title retry. But PlaySong's not-found flow then routes into the song-to-album cascade (AlbumPlay.TryAlbumFallbackAsync, JF-345), which has NO kana awareness: it romanizes at entry (AlbumPlayService.cs:352) and accepts at Math.Max(normal, CrossMediaAlbumThreshold=90). A kana-origin query whose songs were just refused can fuzzy-accept a short Latin ALBUM name at 90+ (plain PartialRatio reaches 90-99 for near-identical strings with no real collision; the JF-652 review established plain 91-99 does not prove collision) and auto-play it. The refused wrong-accept migrates one medium over instead of becoming the honest not-found.

Same family: JF-660 (the artist-side TryEntityFallbackAsync flag inert on the PlaySong/FindSong paths that pass pre-romanized text). Fixing both together makes sense: thread a kanaOrigin flag (or hoist the RomanizeWithOrigin composite on KatakanaRomanizer so raw capture cannot be bypassed) into TryAlbumFallbackAsync and gate its >= 90 acceptance with the JF-654 song bar shape (SongIndexSearch.PassesKanaOriginSongAcceptance, the length-banded collision OR >= 95 leg; note the album score scale may differ, re-derive the plain bar there).

VERIFICATION BAR: a kana-origin PlaySong song=ビートルズ miss ends in the song not-found, never an album auto-play without collision evidence; the Latin song-to-album cascade unchanged (JF-345 matrix).
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
