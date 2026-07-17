---
id: JF-325
title: 'Feature: Voice playlist editing (add/remove current track, create playlist)'
status: To Do
assignee: []
created_date: '2026-07-12 15:00'
updated_date: '2026-07-13 20:17'
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
