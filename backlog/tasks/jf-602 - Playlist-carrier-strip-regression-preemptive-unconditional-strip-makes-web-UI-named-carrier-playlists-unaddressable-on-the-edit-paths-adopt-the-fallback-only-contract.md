---
id: JF-602
title: >-
  Playlist carrier strip regression: preemptive unconditional strip makes
  web-UI-named carrier playlists unaddressable on the edit paths; adopt the
  fallback-only contract
status: In Progress
assignee: []
created_date: '2026-09-20 19:14'
updated_date: '2026-09-20 19:56'
labels: []
dependencies: []
references:
  - commit 560b484c
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaylistNameNormalizer.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlaylistEditHandlerBase.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Gate review of 560b484c (JF-600) confirmed a regression: PlaylistNameNormalizer.NormalizePlaylistName strips a leading carrier word ('chiamata/called/named/genaamd/llamada/namens...') preemptively and unconditionally at all six playlist-slot readers, discarding the raw value. The in-code justification ("the plugin's own create path is the only writer in that namespace") is false: CreatePlaylistIntentHandler calls the server-wide IPlaylistManager, the same manager behind Jellyfin's web UI and .m3u imports. Consequence: a playlist genuinely named 'Named Sessions' (or 'Chiamata Sei', 'genaamd Alles') is no longer addressable on the edit paths: PlaylistEditHandlerBase.FindPlaylist (exact/prefix/substring, no raw re-query) previously matched the raw fill 'named sessions' at the exact tier; the stripped 'sessions' misses all three tiers and the user gets NotFoundPlaylist on AddSong/AddCurrent/RemoveCurrent; Create misses the duplicate check and creates 'Sei' beside 'Chiamata Sei'. The play paths partially survive because the PlayPlaylist/ShufflePlay search uses SearchTerm contains semantics on the live 10.11 box (live-probed: stripped trailing word still surfaces the playlist), so the damage concentrates on the edit family. The sibling strip mechanisms are deliberately fallback-only for exactly this title class: AlbumPlayService.cs:134 (JF-469 "the strip is a FALLBACK, never a preemptive rewrite") and PlayVideoIntentHandler.cs:69 (JF-509 RAW-FIRST). Candidate fix directions (pick one, document): (a) make the edit-family strip fallback-only (try raw first, strip and retry on a confirmed miss), or (b) keep preemptive strip but re-query with the raw name before answering NotFound, or (c) keep preemptive and explicitly document the wider accepted population (weakest). Note interaction: the ja unspaced-carrier task and the strip-consolidation task both touch this file; settle this policy first.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A playlist whose real name starts with a carrier word (e.g. 'Named Sessions' created in the Jellyfin web UI) is addressable by voice on AddSong/AddCurrent/RemoveCurrent and by Create's duplicate check, verified by a unit test that fails on current main
- [ ] #2 The class doc no longer claims the plugin is the only writer of playlist names (IPlaylistManager is server-wide; web UI and .m3u imports also write)
- [ ] #3 PlayPlaylist/ShufflePlay keep working for the stripped-name case (the live-verified SearchTerm contains behavior) - regression tests for both
- [ ] #4 The JF-600 live incident case ('crea una playlist chiamata prova echo' -> AddSong with playlist_target='chiamata prova echo') still resolves to 'prova echo' - existing tests stay green
- [ ] #5 The policy decision (fallback-only vs preemptive-with-raw-requery) is documented on PlaylistNameNormalizer with the accepted trade-offs, consistent with the JF-469/JF-509 sibling contracts
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented 2026-09-20 (uncommitted, under the literal /code-review gate): FindPlaylist now runs every tier on the RAW spoken name first and retries the tiers on the carrier-stripped form only on a miss (the JF-469 fallback-only contract); the class doc names the web-UI/m3u writer population the review surfaced and drops the false "only writer" claim. AddSong/AddCurrent/RemoveCurrent/Create pass the raw slot value to FindPlaylist and keep the normalized name for speech/creation. Regression test: a playlist genuinely named "Called Road Trip" keeps its exact-tier match. Play paths (PlayPlaylist/ShufflePlay) keep the read-time strip; their SearchTerm-based lookup is JF-610 scope.
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
