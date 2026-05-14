---
id: JF-138
title: Move fuzzy match preferences from global to per-user configuration
status: Done
assignee: []
created_date: '2026-05-13 04:29'
updated_date: '2026-05-13 05:35'
labels:
  - configuration
  - per-user
  - fuzzy-match
  - enhancement
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Entities/User.cs
  - Jellyfin.Plugin.AlexaSkill/Configuration/PluginConfiguration.cs
  - Jellyfin.Plugin.AlexaSkill/Configuration/FuzzyMatchBehavior.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/FuzzyMatcher.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Move the three fuzzy match preferences from global `PluginConfiguration` to per-user settings in the `User` entity. Currently all users share the same fuzzy match behavior, but different users may prefer different trade-offs (e.g., one user wants auto-play on fuzzy match, another wants confirmation prompts).

**Properties to move to `User` entity:**
- `FuzzyMatchBehavior` (enum: Confirm/AutoPlay, default: Confirm) — currently global in `PluginConfiguration.FuzzyMatchBehavior`
- `FuzzyMatchThreshold` (int 0-100, default: 60) — currently global in `PluginConfiguration.FuzzyMatchThreshold`  
- `FuzzySuggestionThreshold` (int 0-100, default: 40) — currently global in `PluginConfiguration.FuzzySuggestionThreshold`

**Why**: Different household members have different tolerances for "did you mean?" prompts. One user may prefer auto-play (fewer interruptions), another may want strict matching (no wrong songs). This is a per-user preference, not an admin decision.

**Consumers of these settings:**
- `FuzzyMatcher.cs` reads thresholds from `Plugin.Instance.Configuration`
- `BaseHandler.cs` reads `FuzzyMatchBehavior` from `_config` (PluginConfiguration)

**Migration**: Just remove the global properties from `PluginConfiguration`. Each user sets their own values. No fallback to global needed.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 User entity has FuzzyMatchBehavior, FuzzyMatchThreshold, and FuzzySuggestionThreshold properties
- [ ] #2 FuzzyMatcher reads thresholds from the resolved user
- [ ] #3 BaseHandler fuzzy match logic reads behavior from the resolved user
- [ ] #4 Config page shows fuzzy match preferences per-user in the user skill configuration section
- [ ] #5 Global properties removed from PluginConfiguration
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
## Implementation Plan

### 1. Add properties to `Entities/User.cs`

```csharp
public FuzzyMatchBehavior FuzzyMatchBehavior { get; set; } = FuzzyMatchBehavior.Confirm;
public int FuzzyMatchThreshold { get; set; } = 60;
public int FuzzySuggestionThreshold { get; set; } = 40;
```

Add `using Jellyfin.Plugin.AlexaSkill.Configuration;` for the enum.

### 2. Remove properties from `PluginConfiguration.cs`

- Delete `FuzzyMatchBehavior` (line 80)
- Delete `FuzzyMatchThreshold` (line 114)
- Delete `FuzzySuggestionThreshold` (line 115)
- Delete their validation in `Validate()` (lines 214-222)

### 3. Update `FuzzyMatcher.cs`

**Problem**: `GetDefaultThreshold()` and `GetSuggestionThreshold()` are static methods that read from `Plugin.Instance.Configuration` — they have no user context.

**Solution**: Make them accept a `User` parameter (or change callers to read from the user directly).

Option A — Add user-aware overloads, remove the static config reads:
```csharp
public static int GetDefaultThreshold(Entities.User? user) =>
    user?.FuzzyMatchThreshold ?? DefaultThreshold;

public static int GetSuggestionThreshold(Entities.User? user) =>
    user?.FuzzySuggestionThreshold ?? SuggestionThreshold;
```

Option B — Just remove `GetDefaultThreshold()` and `GetSuggestionThreshold()` and have callers read `user.FuzzyMatchThreshold` directly. Simpler, fewer indirection layers.

### 4. Update `BaseHandler.cs`

Three call sites need the resolved user:

**Line 682** — `FuzzyMatch<T>()` (static method, no user access):
- Make it non-static or pass user/threshold explicitly
- Change `FuzzyMatcher.GetDefaultThreshold()` → `user.FuzzyMatchThreshold`

**Line 724** — `HandleFuzzyMiss<T>()` (instance method, has `_config` access):
- Add `Entities.User user` parameter
- Change `FuzzyMatcher.GetSuggestionThreshold()` → `user.FuzzySuggestionThreshold`
- Change `_config.FuzzyMatchBehavior` → `user.FuzzyMatchBehavior`
- Change `FuzzyMatcher.GetDefaultThreshold()` → `user.FuzzyMatchThreshold`

**Line 734** — the auto-accept check:
- Change `_config.FuzzyMatchBehavior == FuzzyMatchBehavior.AutoPlay` → `user.FuzzyMatchBehavior == FuzzyMatchBehavior.AutoPlay`

**Callers of FuzzyMatch/HandleFuzzyMiss**: All intent handlers that call these methods already receive `Entities.User user` from `HandleAsync`. Thread it through. Search handlers to update:
- Run `grep -n "FuzzyMatch\|HandleFuzzyMiss" Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/*.cs` to find all callers.

### 5. Update `ConfigurationController.cs`

**PATCH `user-skills/{userId}`** (line 60-87): Currently only handles `InvocationName`. Extend to also accept and persist `FuzzyMatchBehavior`, `FuzzyMatchThreshold`, `FuzzySuggestionThreshold` from the request body. The deserialization uses `Dictionary<string, string>` — add parsing for the three new fields.

### 6. Update `config.html`

**Remove from global sections:**
- Remove the `FuzzyMatchBehavior` dropdown (lines 46-53) from the "Server Settings" form
- Remove `FuzzyMatchThreshold` input (lines 178-181) and `FuzzySuggestionThreshold` input (lines 183-186) from "Search Preferences" section
- Remove their save logic (lines 469, 501-504)

**Add to per-user row** in `createUserRow()` (line 302):
- Add a new `<td>` with a FuzzyMatchBehavior select dropdown (Confirm/AutoPlay)
- Add two number inputs for FuzzyMatchThreshold (0-100, default 60) and FuzzySuggestionThreshold (0-100, default 40)
- Pre-populate from `user.FuzzyMatchBehavior`, `user.FuzzyMatchThreshold`, `user.FuzzySuggestionThreshold`
- Include in the PATCH payload when saving (lines 546-548)

**Update table columns**: The user table currently has columns: User | Skill ID | Invocation Name | Status | Token | Action. Add a "Fuzzy Match" column (or use an expandable section to avoid horizontal overflow).

### 7. Update config.html save/load

- **Load** (around line 234, 261-262): Remove global fuzzy match field population
- **Save** (around line 469, 501-504): Remove global fuzzy match field serialization
- **User row save** (line 506+): Include `FuzzyMatchBehavior`, `FuzzyMatchThreshold`, `FuzzySuggestionThreshold` in the PATCH payload per user

### Key Files to Modify

| File | Change |
|------|--------|
| `Entities/User.cs` | Add 3 fuzzy match properties |
| `Configuration/PluginConfiguration.cs` | Remove 3 global properties + validation |
| `Alexa/FuzzyMatcher.cs` | Update `GetDefaultThreshold()` / `GetSuggestionThreshold()` to accept User |
| `Alexa/Handler/BaseHandler.cs` | Thread user into `FuzzyMatch()` and `HandleFuzzyMiss()` |
| `Alexa/Handler/Intent/*.cs` | Pass user to FuzzyMatch/HandleFuzzyMiss calls |
| `Controller/ConfigurationController.cs` | PATCH endpoint accepts fuzzy match fields |
| `Configuration/config.html` | Move fuzzy UI from global to per-user row |
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
