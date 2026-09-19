---
id: JF-325
title: 'Feature: Voice playlist editing (add/remove current track, create playlist)'
status: Done
assignee: []
created_date: '2026-07-12 15:00'
updated_date: '2026-09-19 01:40'
labels:
  - feature
  - playlists
milestone: m-10
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/
  - Jellyfin.Plugin.AlexaSkill/Alexa/IntentNames.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Today users can PLAY playlists but cannot manage them — `IPlaylistManager` is never referenced in the codebase (functional review 2026-07-12). Adding voice curation turns the skill from consumption-only into a real music-management surface, matching what AskNavidrome/the Python jellyfin_alexa_skill offer.

Deliver new intents backed by Jellyfin's playlist CRUD:
- "add this to my {playlist}" / "add {song} to {playlist}"
- "create a playlist called {name}"
- "remove this from {playlist}"

Resolve "this" from the current AudioPlayer token (prefer context.AudioPlayer.Token per CLAUDE.md, since FullNowPlayingItem is cleared). Respect per-user library/content gating and operate on the linked Jellyfin user's playlists. New intents need handler + IntentNames entry + interaction-model samples (all 17 locales, it-IT via YAML) + 17 locale response strings + unit/NLU tests (per the new-intent skill).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 User can add the currently-playing track to a named playlist by voice
- [ ] #2 User can add a named song to a named playlist by voice
- [ ] #3 User can create a new playlist by name
- [ ] #4 User can remove the currently-playing track from a named playlist
- [ ] #5 'this' resolves from the AudioPlayer token, not FullNowPlayingItem
- [ ] #6 Operations target the linked Jellyfin user's playlists and respect library/content gating
- [ ] #7 Samples + response strings added to all 17 locales; unit and NLU tests included
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped, deployed, live-verified. 4 new intents across all 17 locales - AddCurrentToPlaylist, AddSongToPlaylist, RemoveCurrentFromPlaylist, CreatePlaylist - backed by IPlaylistManager with semantics verified against the PlaylistManager source ("N"-format GUIDs on remove; server-side dedupe on add; dedicated PlaylistCreateFailed tell when no Playlists folder; duplicate names refused with PlaylistAlreadyExists instead of silent "name1"). "this" resolves token-first via StreamTokenCodec.TryGetItemId (the /simplify pass caught that raw Guid.TryParse breaks on composite "{guid}|sleep:" tokens) with session fallback, pinned by unit tests. AddSong uses the bounded PlaySong pattern (SearchTerm + exact/prefix; the first draft's full-Audio fuzzy scan was the review's top finding and is banned) and gates on the song-index warm-up (WarmingGateCoverageTests roster). playlist_target slot is AMAZON.MusicRecording (SearchQuery cannot combine with another slot per sample; playlist is SearchQuery-typed elsewhere). JELLYFIN_12 DefineConstants in both csprojs bridges the AddItemToPlaylistAsync divergence. Gate evidence: /simplify skill invoked on the final state (1 real finding applied: the codec swap; GetSlotValue hoist filed as JF-594 per the same-turn rule); code-review high via feature-dev:code-reviewer with 3 critical + several important findings ALL applied (bounded search, awaited writes, stray artifact, gate ordering, fixtures, per-type file split, localized hints). Suite 4135/4135 both TFMs (+10); Release 0 warnings; all validators PASS; NLU fixtures +4 rows (dry-run validated). LIVE: deployed, custom-model rebuild 17/17 locales succeeded (first attempt transient outbound SSL to SMAPI, retry clean), simulator smokes green (AddCurrent-nothing-playing tell; Create created a real playlist end-to-end then deleted, library verified clean). Open: live profile-nlu probe of the two-free-text-slot AddSong sample when SMAPI auth is convenient; on-device listen.
<!-- SECTION:FINAL_SUMMARY:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
