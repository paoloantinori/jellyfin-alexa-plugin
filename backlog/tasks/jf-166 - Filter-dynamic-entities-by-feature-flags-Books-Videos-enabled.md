---
id: JF-166
title: Filter dynamic entities by feature flags (Books/Videos enabled)
status: Done
assignee:
  - agent-jf166
created_date: '2026-05-17 11:20'
updated_date: '2026-05-17 12:24'
labels:
  - bug
  - config
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntityBuilder.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`DynamicEntityBuilder.BuildLastPlayedValues` queries across all media types (Audio, Movie, Episode, AudioBook) unconditionally. If a user has disabled books or videos via `BooksEnabled`/`VideosEnabled` config flags, book or episode items from last-played history will still be injected into the NLU. This could cause Alexa to resolve a spoken title to an entity, route to the handler, and then get rejected by the feature flag — confusing UX.

Note: Jellyfin treats AudioBook as a "books" content type (gated by `BooksEnabled`), NOT as music. See `BaseHandler.IsTypeAllowed`.

Requires injecting `PluginConfiguration` into `DynamicEntityBuilder` (DI scope change since it's a singleton that currently resolves config live from `Plugin.Instance`). Filter `IncludeItemTypes` in `BuildLastPlayedValues` and the series/book builders based on enabled features.
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added feature flag filtering to DynamicEntityBuilder. The Build() method now gates artist/album queries by MusicEnabled, series queries by VideosEnabled, and audiobook queries by BooksEnabled. BuildLastPlayedValues dynamically builds IncludeItemTypes based on enabled flags. Uses config?.Flag != false pattern to default to enabled when Plugin.Instance is null (unit test scenario). All 1516 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
