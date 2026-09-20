---
id: JF-600
title: >-
  Playlist create leak: "chiamata" carrier captured into playlist_target and the
  empty-song branch asks the wrong question
status: Done
assignee: []
created_date: '2026-09-20 17:04'
updated_date: '2026-09-20 18:30'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
On-device report 2026-09-20 (Paolo, test battery item 4): "crea una playlist chiamata prova echo" routed to AddSongToPlaylistIntent with playlist_target="chiamata prova echo" (the Italian "called" carrier leaked into the slot) and the song slot EMPTY. The handler hit the missing-slot branch and answered DidNotCatchPlaylistName ("Non ho capito il nome della playlist. Quale playlist vorresti ascoltare?"), which is also the wrong prompt for the user's intent (they wanted CREATE, not listen/add).

Two defects to fix:
1. Carrier leak: the it-IT samples for the playlist intents must carry the "chiamata/chiamato" (called) qualifier in a way that keeps it OUT of the playlist_target slot, or the handlers must strip a leading "chiamata/chiamato" (and locale equivalents) from the raw slot value before matching. Handler-side stripping is locale-general and survives model drift; model-side is cleaner ASR but per-locale. Decide: likely handler-side strip + it-IT sample audit.
2. Empty-song handling on AddSongToPlaylistIntent: when song is empty but audio is playing, the natural interpretation is "add the currently playing song" (AddCurrentSongToPlaylist behavior); the current branch instead demands a playlist name and asks an ascoltare-phrased question. At minimum the prompt wording must match the action; better, route to add-current when NowPlaying exists.

Repro: simulator AddSongToPlaylistIntent {playlist_target: "chiamata prova echo", song: ""} -> DidNotCatchPlaylistName. Live evidence in podman logs 2026-09-19/20 window.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implementation (2026-09-20):
- Handler side: PlaylistEditHandlerBase.NormalizePlaylistName + per-language carrier table (it/en/de/es/fr/pt/nl/ar/hi/ja; ja strips trailing という). Applied at 5 slot-read sites: AddSong (playlist_target), AddCurrent, RemoveCurrent, Create (playlist), PlayPlaylist (inline read, cross-namespace call). Create path now creates "prova echo" not "chiamata prova echo".
- Branch split in AddSong: playlist-missing -> SpecifyPlaylistName (neutral retry, edit-family only; DidNotCatchPlaylistName stays with PlayPlaylist/ShufflePlay), song-missing -> SpecifySongForPlaylist with the STRIPPED playlist name.
- Model side: carrier-optional CreatePlaylistIntent samples appended in all 17 templates (e.g. it "Crea playlist chiamata {playlist}", "Crea una playlist {playlist}"; en "create playlist {playlist}"...), regenerated all 17 models, validator PASS, VOICE_COMMANDS.md regenerated.
- Strings: SpecifyPlaylistName + SpecifySongForPlaylist in all 17 locale JSONs (validate_locales PASS).
- Tests: 5 handler tests + 11-case normalizer Theory in PlaylistEditIntentHandlerTests; NLU fixtures +2 it-IT rows.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped 2026-09-20 (commit 'fix(playlist): JF-600 carrier leak stripped, create routing widened, empty-slot prompts split'). Live-verified after deploy: profile-nlu routes 'crea playlist chiamata prova echo' / 'crea playlist prova echo' / 'crea una playlist prova echo' all to CreatePlaylistIntent with playlist='prova echo' (previously the first two fell into the AddSong free-text steal); simulator with the incident shape playlist_target='chiamata prova echo' + empty song now answers 'Quale canzone vuoi aggiungere alla playlist prova echo?' (was the listen-worded DidNotCatchPlaylistName); simulator Create with playlist='chiamata prova echo' creates the playlist named 'prova echo' (was: would create 'chiamata prova echo'). All 17 locale models rebuilt (fr-CA needed one solo retry, the known trainer flake). Gates: /simplify (home moved to Util/PlaylistNameNormalizer, ShufflePlay 6th site rewired, dead guards removed, separators baked) + code-review high (hi-IN trailing carrier added, preemptive-strip trade-off documented); suite 4181/4181 both TFMs.
<!-- SECTION:FINAL_SUMMARY:END -->
