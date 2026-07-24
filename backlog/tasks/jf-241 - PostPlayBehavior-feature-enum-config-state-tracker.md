---
id: JF-241
title: 'PostPlayBehavior feature: enum, config, state tracker'
status: Done
assignee: []
created_date: '2026-06-02 12:05'
updated_date: '2026-06-02 12:35'
labels:
  - feature
  - tdd
dependencies: []
references:
  - /home/pantinor/.cc-mirror/zai/config/plans/cozy-sleeping-wreath.md
documentation:
  - Configuration/SearchResponseMode.cs
  - Alexa/RadioModeState.cs
  - Alexa/Handler/BaseHandler.cs (GetSearchResponseMode pattern)
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create the foundational types for the PostPlayBehavior feature:

1. **Enum** `Configuration/PostPlayBehavior.cs` — `Stop=0, AutoPlay=1, Ask=2` with XML docs. Follow `SearchResponseMode.cs` pattern.

2. **State tracker** `Alexa/PostPlayState.cs` — Static class with ConcurrentDictionary keyed by `userId:deviceId`. Stores `{ Mode, ItemId, Timestamp }`. Methods: `Set()`, `TryGet()` (with 2-min TTL staleness), `Remove()`, `Clear()`. Follow `RadioModeState.cs` pattern.

3. **Config property** in `PluginConfiguration.cs` — `PostPlayBehavior DefaultPostPlayBehavior { get; set; } = PostPlayBehavior.Stop;`

4. **Per-user override** in `Entities/User.cs` — `PostPlayBehavior? PostPlayBehavior { get; set; }` (nullable, null = use global)

5. **Helper** in `BaseHandler.cs` — `GetPostPlayBehavior(Entities.User? user)` returning per-user override → global default. Follow `GetSearchResponseMode` pattern.

6. **Config UI** in `config.html` — `<select>` dropdown for DefaultPostPlayBehavior in Playback Preferences section. JS load/save handlers.

7. **Per-user config** in `ConfigurationController.cs` — Handle PostPlayBehavior in UpdateUserSkill PATCH, same pattern as SearchResponseMode. Add per-user dropdown in User table.

**Locale strings** (all 17 JSON files):
- PostPlayAutoPlayAnnouncement: "Playing more music by {0}"
- PostPlayAskPrompt: "Would you like to hear more music like this?"
- PostPlayAskReprompt: "You can say yes to hear more, or no to stop."
- PostPlayNoResponse: "Alright, stopping playback."

Non-en-US locales use English placeholders.

**Write unit tests first (TDD)**:
- PostPlayBehavior enum default value
- PostPlayState Set/TryGet/Remove/Clear lifecycle
- PostPlayState TTL staleness (>2 min returns false)
- GetPostPlayBehavior per-user override vs global default
- Config UI renders and saves correctly
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 PostPlayBehavior enum has Stop=0, AutoPlay=1, Ask=2
- [ ] #2 PostPlayState stores/retrieves mode and itemId per userId:deviceId
- [ ] #3 PostPlayState entries expire after 2 minutes
- [ ] #4 PluginConfiguration.DefaultPostPlayBehavior defaults to Stop
- [ ] #5 User.PostPlayBehavior is nullable (null = use global default)
- [ ] #6 GetPostPlayBehavior returns per-user when set, global default otherwise
- [ ] #7 config.html shows PostPlayBehavior dropdown and saves selection
- [ ] #8 All 17 locale JSON files have PostPlayAutoPlayAnnouncement, PostPlayAskPrompt, PostPlayAskReprompt, PostPlayNoResponse keys
- [ ] #9 dotnet build passes with zero warnings
- [ ] #10 Unit tests pass for PostPlayState lifecycle, TTL, and config resolution
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Completed JF-241: Created PostPlayBehavior enum (Stop/AutoPlay/Ask), PostPlayState in-memory tracker with TTL, config property with per-user override, GetPostPlayBehavior helper, config.html dropdown with per-user support, ConfigurationController PATCH handling, and locale strings in all 17 JSON files. All 21 TDD tests pass, full suite 2087/2087 green, locale validation passes.
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
