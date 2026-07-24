---
id: JF-326
title: 'Feature: Star ratings by voice ("rate this 5 stars")'
status: To Do
assignee: []
created_date: '2026-07-12 15:00'
updated_date: '2026-07-13 20:18'
labels:
  - feature
  - ratings
milestone: m-10
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/MarkFavoriteIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The only rating mechanism today is favorite/unfavorite (MarkFavorite/UnmarkFavorite). There is no star rating (functional review 2026-07-12). A "rate this 5 stars" / "give this 2 stars" intent writes Jellyfin `UserItemData.Rating`, which then feeds Jellyfin's recommendation and sort logic (the plugin already uses PlayCount+Rating sort in PlayArtistSongsIntent selection). Cheap, natural, and improves the discovery loop.

New intent: resolve the current item from context.AudioPlayer.Token, parse a 0–5 (or 0–10, match Jellyfin's scale) rating slot, write UserItemData for the linked user. Confirm back by voice. Handler + IntentNames + samples (17 locales, it-IT YAML) + 17 response strings + unit/NLU tests.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 User can rate the currently-playing item by voice (e.g. 'rate this 5 stars')
- [ ] #2 The rating is written to Jellyfin UserItemData for the linked user on the correct item (resolved via AudioPlayer token)
- [ ] #3 The rating scale matches Jellyfin's expected range and is validated (out-of-range handled gracefully)
- [ ] #4 Alexa confirms the rating back to the user
- [ ] #5 Samples + response strings across all 17 locales; unit and NLU tests included
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
